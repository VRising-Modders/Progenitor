using System;
#if !PROGENITOR_TESTS
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using ProjectM;
using ProjectM.UI;
using Progenitor.Events;
using Progenitor.Persistence;
using Stunlock.Core;
using Stunlock.Network;
using Unity.Collections;
using UnityEngine;
using UnityObject = UnityEngine.Object;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// Client-only debug-bridge ops that drive the main-menu join and host paths. Direct Connect and
/// Private Game / Host New both end at <c>GameHelper.LaunchGame</c> with a
/// <c>GameConnect</c>; calling that from the bridge is the same hand-off as the click, and
/// it does not need a live ECS world. <c>ClientBootstrapSystem.Connect</c> is the later netcode
/// call (world + Steam ticket) and is not used here.
/// </summary>
public static class ConnectOps
{
    /// <summary>Default Direct-Connect transport; <c>ConnectAddress.CreateSteamIPv4</c>.</summary>
    public const string ProtocolSteamIPv4 = "SteamIPv4";

    /// <summary>
    /// LanMode listen; <c>ConnectAddress.CreateLidgrenIPv4</c>. The staged test server
    /// in LanMode only opens this socket, so the Steam client must pass it to join.
    /// </summary>
    public const string ProtocolLidgren = "Lidgren";

    /// <summary>
    /// Direct-Connect transport name. Omitted / empty is <see cref="ProtocolSteamIPv4"/>
    /// (unchanged default). <see cref="ProtocolLidgren"/> builds the address with
    /// <c>ConnectAddress.CreateLidgrenIPv4</c> — the same factory as the hosted
    /// <c>FallbackConnectAddress</c>. Comparison is case-insensitive; the result echoes
    /// the canonical spelling. Other values (including a <c>Lidgren://</c> address scheme)
    /// are refused: the address parser still only strips <c>SteamIPv4://</c>.
    /// </summary>
    public static string ParseConnectProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return ProtocolSteamIPv4;

        var trimmed = protocol.Trim();
        if (trimmed.Equals(ProtocolSteamIPv4, StringComparison.OrdinalIgnoreCase))
            return ProtocolSteamIPv4;
        if (trimmed.Equals(ProtocolLidgren, StringComparison.OrdinalIgnoreCase))
            return ProtocolLidgren;

        throw new ArgumentException(
            $"args.protocol must be {ProtocolSteamIPv4} or {ProtocolLidgren} (got '{protocol}')");
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// <see cref="GameHelper.LaunchGame"/> returns as soon as the scene load starts;
    /// <c>Client_0</c> appears later. The bridge in-flight flag is cleared when that call
    /// returns, so a second connect/host in the load gap would stack scene loads. This flag
    /// covers that gap: set just before LaunchGame, cleared on client <see cref="WorldReady"/>
    /// or after <see cref="LaunchTimeoutSeconds"/>.
    /// </summary>
    private const double LaunchTimeoutSeconds = 120;

    /// <summary>
    /// Filename (no extension) of a Host New difficulty TextAsset. Applied only when the
    /// caller passes <c>args.difficulty</c>; the captured Private Game menu left
    /// <c>GameDifficultyPreset</c> / <c>GameSettingsPreset</c> empty.
    /// </summary>
    private const string DefaultDifficultyPresetName = "Difficulty_Normal";

    /// <summary>
    /// Shipped hosted-game ports, captured live from the Private Game menu
    /// (<c>HostPort</c> / <c>QueryPort</c> / <c>HostFallbackPort</c> = 9876 / 9877 / 9878).
    /// The live dedicated server on this machine also binds 9876; the op refuses that
    /// default when a UDP listener is already there. Pass <c>args.port</c> (e.g. 9886)
    /// to use 9886 / 9887 / 9888 instead.
    /// </summary>
    private const int DefaultHostPort = 9876;
    private const int DefaultQueryPort = 9877;
    private const int DefaultFallbackPort = 9878;
    private const int DefaultHostMaxUsers = 40;

    private static int _launchInFlight;
    private static int _launchGeneration;
    private static IDisposable? _worldReadySub;

    /// <summary>
    /// In-memory <see cref="ClientHostSettings"/> snapshot taken just before LaunchGame so the
    /// host op can put <c>args.port</c> onto the child's argv and restore the player's values
    /// once the child has spawned. Must not be restored when LaunchGame returns: that call
    /// returns as soon as the scene load starts, and <c>GameBootstrap.Start</c> builds the
    /// child argv later from the still-mutated singleton.
    /// </summary>
    private static ClientHostSnapshot _clientHostSnapshot;
    private static int _clientHostRestoreGeneration;

    /// <summary>
    /// Drop the WorldReady watch and release the launch-in-flight slot. Called from the Dev
    /// plugin Unload; a reloadable consumer is not expected, but the subscription must not
    /// outlive the plugin.
    /// </summary>
    internal static void Shutdown()
    {
        try { RestoreClientHostSettings(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"connect: ClientHostSettings restore on unload: {e.Message}"); }
        try { _worldReadySub?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"connect: WorldReady unsubscribe: {e.Message}"); }
        _worldReadySub = null;
        Interlocked.Exchange(ref _launchInFlight, 0);
        Interlocked.Increment(ref _launchGeneration);
    }

    /// <summary>
    /// Build a Direct-Connect <see cref="GameConnect"/> and call <see cref="GameHelper.LaunchGame"/>.
    /// Client, main thread, no world required — <see cref="Progenitor.Worlds.Client"/> must be null
    /// (already in a game is an error). A second call while a launch is in flight (scene loading,
    /// no <c>Client_0</c> yet) is also an error. Join is async; the caller watches <c>ping</c>
    /// for <c>Client_0</c>. Address accepts IPv4, <c>host:port</c>, and <c>SteamIPv4://host:port</c>;
    /// an explicit <c>args.port</c> wins over a port split from the address.
    /// Optional <c>args.protocol</c> is <c>SteamIPv4</c> (default) or <c>Lidgren</c>; a LanMode
    /// server only opens a Lidgren socket, so the Steam client must pass Lidgren to join it.
    /// </summary>
    public static object Connect(JsonElement args)
    {
        bool launched = false;
        try
        {
            if (Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("connect runs on the client side only");

            if (Progenitor.Worlds.Client != null)
                throw new InvalidOperationException("already in a game");

            var address = GetString(args, "address");
            var parsed = ConnectAddressParser.Parse(address, TryGetOptionalInt(args, "port"));

            var password = GetString(args, "password");
            if (string.IsNullOrEmpty(password))
                password = null;

            var protocol = ParseConnectProtocol(GetString(args, "protocol"));

            BeginLaunch();
            launched = true;

            // Quote-verified: ConnectAddress.CreateSteamIPv4(string, ushort) and
            // GameHelper.LaunchGame(GameConnect) by value. Do not pass GameConnect as a generic
            // argument (ValueType-wrapper trap). Hosted loopback and the test server use SteamIPv4;
            // Lidgren is a live-join fallback, not a guess for the first attempt.
            // args.protocol "Lidgren" is that explicit fallback: CreateLidgrenIPv4(host, port),
            // same factory as the hosted GameConnect.FallbackConnectAddress (LanMode listen).
            var addr = string.Equals(protocol, ProtocolLidgren, StringComparison.Ordinal)
                ? ConnectAddress.CreateLidgrenIPv4(parsed.Host, (ushort)parsed.Port)
                : ConnectAddress.CreateSteamIPv4(parsed.Host, (ushort)parsed.Port);
            var connect = new GameConnect
            {
                ConnectAddress = addr,
                CreateClient = true,
                ShouldHost = false,
            };
            if (password != null)
                connect.ConnectPassword = new FixedString128Bytes(password);

            CollectBeforeOwnLaunch("connect");
            GameHelper.LaunchGame(connect);
            Plugin.Logger?.LogInfo($"connect: LaunchGame {protocol}://{parsed.Host}:{parsed.Port} password={(password != null ? "yes" : "no")}");

            return new
            {
                ok = true,
                address = parsed.Host,
                port = parsed.Port,
                protocol,
                note = "LaunchGame invoked; join is async. Watch ping.worlds for Client_0.",
            };
        }
        catch (Exception e)
        {
            if (launched) AbortLaunch();
            Plugin.Logger?.LogError($"connect op threw: {e}");
            throw;
        }
    }

    /// <summary>
    /// Build a Private-Game / Host-New <see cref="GameConnect"/> and call
    /// <see cref="GameHelper.LaunchGame"/>. Client, main thread, no world required —
    /// <see cref="Progenitor.Worlds.Client"/> must be null. Shares the connect launch-in-flight
    /// slot: a second host/connect while the scene is loading is an error. The child server
    /// spawn and loopback join are async; the caller watches <c>ping</c> for <c>Client_0</c>.
    /// </summary>
    /// <remarks>
    /// Live Private Game capture (2026-09-03, evidence §21) is the field map. Three earlier
    /// <c>host</c> runs NREd in <c>GameBootstrap.Start</c> because they set
    /// <c>HostDedicated = false</c> (in-process host). The menu sets <c>HostDedicated = true</c>
    /// (spawn bundled <c>VRisingServer.exe</c>), <c>SavePerUser = true</c>, empty
    /// <c>HostAddress</c>, Lidgren fallback on <c>HostFallbackPort</c>, and leaves both
    /// settings-preset names empty. Evidence §24: child argv <c>-gamePort/-queryPort/-secure</c>
    /// come from <c>SettingsManager.ClientSettings.HostSettings</c>
    /// (<see cref="ClientHostSettings"/>: Port, QueryPort, Secure — no FallbackPort, no LanMode),
    /// not from <see cref="GameConnect.HostPort"/>. This op writes those fields before
    /// LaunchGame and restores them on client WorldReady.
    /// </remarks>
    public static object Host(JsonElement args)
    {
        bool launched = false;
        bool launchGameInvoked = false;
        try
        {
            if (Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("host runs on the client side only");

            if (Progenitor.Worlds.Client != null)
                throw new InvalidOperationException("already in a game");

            var saveName = GetString(args, "saveName");
            if (string.IsNullOrWhiteSpace(saveName))
                saveName = "progenitor-hosted";
            saveName = saveName.Trim();
            ValidateSaveName(saveName);

            var password = GetString(args, "password");
            if (string.IsNullOrEmpty(password))
                password = null;

            // Captured menu: ServerHostSettings.LanMode=False. args.lan still overrides.
            bool lan = GetBool(args, "lan", false);

            var ports = ResolveHostPorts(args);
            RefuseIfDefaultPortBusy(ports.HostPort);

            // Host New's Init_HostNewServer is one of the few client call sites of
            // VerifyServerGameSettings (CallerCount 5). Keep the Verify so nested modifier
            // classes exist; do not write GameDifficultyPreset unless args.difficulty is set
            // (the capture left both preset names empty).
            EnsureSettingsSingletons();

            var persistRoot = ReadPersistentDataPath();
            var saveDirectory = ResolveHostSaveDirectory(persistRoot, saveName);
            bool saveDirectoryExisted = Directory.Exists(saveDirectory);
            bool saveDirectoryCreated = EnsureHostSaveDirectory(saveDirectory, persistRoot);
            string? sessionId = EnsureSessionIdFile(saveDirectory);
            string? startDate = EnsureStartDateFile(saveDirectory);

            var hostWritten = ApplyInMemoryHostSettings(
                saveName, password, lan, ports.HostPort, ports.QueryPort, DefaultHostMaxUsers, ports.FallbackPort);
            EnsureNestedHostObjects(hostWritten);

            var difficultyArg = GetString(args, "difficulty");
            var difficulty = string.IsNullOrWhiteSpace(difficultyArg)
                ? ClearDifficultyPresets(hostWritten)
                : ApplyDifficultyPreset(hostWritten, difficultyArg);

            var persistResult = PersistSettingsIntoSaveFolder(saveDirectory, hostWritten);
            var nullProbe = ProbeStartNulls(hostWritten);

            // Captured menu used HostNewGame even on a Play → Private Game (continue) click.
            const StunAnalytics.Client.ServerJoinMethod joinMethod =
                StunAnalytics.Client.ServerJoinMethod.HostNewGame;

            BeginLaunch();
            launched = true;

            // Live §24: GameBootstrap.Start builds the child argv from
            // SettingsManager.ClientSettings.HostSettings (Port / QueryPort / Secure), not from
            // GameConnect.HostPort. LaunchGame returns before Start, so these must stay until
            // the child is spawned (WorldReady) rather than being restored when LaunchGame returns.
            var clientHost = ApplyClientHostSettings(ports.HostPort, ports.QueryPort);

            // Quote-verified live (host_capture LaunchGame prefix, evidence §21):
            // ConnectAddress SteamIPv4://127.0.0.1:<HostPort>, FallbackConnectAddress
            // Lidgren://127.0.0.1:<HostFallbackPort>, HostAddress "", HostDedicated true,
            // SavePerUser true, HostMaxUsers 40, CreateClient true, AnalyticsJoinMethod HostNewGame.
            // ConnectAddress is a 112-byte C# struct (unmanaged); Nullable_Unboxed<ConnectAddress>
            // is already the GameConnect field type, so the IL2CPP instantiation exists. Do not
            // call ConnectAddress.ToString() (interop ToString).
            var fallback = ConnectAddress.CreateLidgrenIPv4("127.0.0.1", ports.FallbackPort);
            var connect = new GameConnect
            {
                ConnectAddress = ConnectAddress.CreateSteamIPv4("127.0.0.1", ports.HostPort),
                FallbackConnectAddress = new Il2CppSystem.Nullable_Unboxed<ConnectAddress>(fallback),
                ServerSaveName = new FixedString512Bytes(saveName),
                HostAddress = new FixedString32Bytes(""),
                HostMaxUsers = DefaultHostMaxUsers,
                HostPort = ports.HostPort,
                HostFallbackPort = ports.FallbackPort,
                PlayReplay = false,
                CreateClient = true,
                ShouldHost = true,
                HostDedicated = true,
                SavePerUser = true,
                AnalyticsJoinMethod = joinMethod,
            };
            if (password != null)
                connect.ConnectPassword = new FixedString128Bytes(password);

            var connectDump = DumpGameConnect(connect, saveName, password != null, joinMethod);
            var hostDump = DumpHostSettings(
                hostWritten, saveName, password != null, lan, ports.HostPort, ports.QueryPort, DefaultHostMaxUsers, ports.FallbackPort);
            var clientHostDump = DumpClientHostSettings(clientHost, ports.HostPort, ports.QueryPort);

            CollectBeforeOwnLaunch("host");
            GameHelper.LaunchGame(connect);
            launchGameInvoked = true;
            Plugin.Logger?.LogInfo(
                $"host: LaunchGame ShouldHost HostDedicated saveName={saveName} saveDir={saveDirectory} " +
                $"createdDir={saveDirectoryCreated} existed={saveDirectoryExisted} " +
                $"joinMethod={joinMethod} port={ports.HostPort} queryPort={ports.QueryPort} " +
                $"fallbackPort={ports.FallbackPort} portFromArgs={ports.FromArgs} lan={lan} " +
                $"password={(password != null ? "yes" : "no")} maxUsers={DefaultHostMaxUsers} " +
                $"sessionId={(sessionId ?? "(none)")} startDate={startDate ?? "(none)"} " +
                $"difficulty={difficulty.PresetName} applied={difficulty.Applied} source={difficulty.Source} " +
                $"hostJson={persistResult.HostSettingsPath ?? "(skipped)"} " +
                $"gameJson={persistResult.GameSettingsPath ?? "(skipped)"}");
            Plugin.Logger?.LogInfo($"host: GameConnect {connectDump}");
            Plugin.Logger?.LogInfo($"host: ServerHostSettings {hostDump}");
            Plugin.Logger?.LogInfo($"host: ClientHostSettings {clientHostDump}");
            Plugin.Logger?.LogInfo($"host: difficulty {difficulty.LogLine}");
            Plugin.Logger?.LogInfo($"host: nullProbe {nullProbe.LogLine}");

            return new
            {
                ok = true,
                saveName,
                saveDirectory,
                saveDirectoryCreated,
                saveDirectoryExisted,
                sessionId,
                startDate,
                port = ports.HostPort,
                queryPort = ports.QueryPort,
                fallbackPort = ports.FallbackPort,
                portFromArgs = ports.FromArgs,
                analyticsJoinMethod = joinMethod.ToString(),
                gameConnect = connectDump,
                serverHostSettings = hostDump,
                clientHostSettings = clientHostDump,
                difficulty,
                nullProbe,
                wroteHostSettings = persistResult.HostSettingsPath,
                wroteGameSettings = persistResult.GameSettingsPath,
                note = "LaunchGame invoked (ShouldHost, HostDedicated); child server spawn and join are async. " +
                       $"Watch ping.worlds for Client_0. saveDir={saveDirectory} " +
                       $"created={saveDirectoryCreated} joinMethod={joinMethod} " +
                       $"port={ports.HostPort}/{ports.QueryPort}/{ports.FallbackPort} " +
                       $"difficulty={difficulty.PresetName}.",
            };
        }
        catch (Exception e)
        {
            // Restore ClientHostSettings only when LaunchGame never ran: Start will not
            // build argv. After LaunchGame returns, Start still needs the mutated ports
            // (evidence §24); WorldReady / the 120 s timeout restore those.
            if (launched) AbortLaunch(restoreClientHost: !launchGameInvoked);
            Plugin.Logger?.LogError($"host op threw: {e}");
            throw;
        }
    }

    /// <summary>
    /// Core's join-hang mitigation for a Bootstrap without the LaunchGame slot: a no-op when
    /// Core's slot is armed (it collects inside LaunchGame itself) or the gate is off. The Core
    /// call sits in its own non-inlined method so a Dev newer than Core (no
    /// <c>GcBeforeLaunch</c>) fails here, inside the catch, instead of failing the whole op.
    /// </summary>
    private static void CollectBeforeOwnLaunch(string caller)
    {
        try { CallCoreGcBeforeLaunch(caller); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"{caller}: GcBeforeLaunch unavailable: {e.Message}"); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CallCoreGcBeforeLaunch(string caller) =>
        Progenitor.Networking.GcBeforeLaunch.BeforeOwnLaunch(caller);

    /// <summary>Request the production ExitGame view without focus or OS input; completion is asynchronous.</summary>
    public static object Disconnect() => Disconnect(default);

    /// <summary>statusOnly observes without initiating a leave.</summary>
    public static object Disconnect(JsonElement args)
    {
        bool statusOnly = false;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("statusOnly", out var value))
        {
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new ArgumentException("args.statusOnly must be a boolean");
            statusOnly = value.GetBoolean();
        }
        return LeaveGameOps.Request(statusOnly, Volatile.Read(ref _launchInFlight) != 0);
    }

    private static void BeginLaunch()
    {
        EnsureWorldReadyWatch();
        if (Interlocked.CompareExchange(ref _launchInFlight, 1, 0) != 0)
            throw new InvalidOperationException("launch already in progress");

        int gen = Interlocked.Increment(ref _launchGeneration);
        try
        {
            MainThread.PostDelayed(LaunchTimeoutSeconds, () =>
            {
                try
                {
                    if (Volatile.Read(ref _launchGeneration) != gen) return;
                    if (Interlocked.CompareExchange(ref _launchInFlight, 0, 1) == 1)
                        Plugin.Logger?.LogWarning("connect: launch-in-flight cleared after 120 s timeout (no client world)");
                    RestoreClientHostSettings(gen);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"connect: launch timeout threw: {e}");
                }
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"connect: could not schedule 120 s launch timeout: {e.Message}");
        }
    }

    private static void AbortLaunch(bool restoreClientHost = true)
    {
        if (restoreClientHost)
            RestoreClientHostSettings(Volatile.Read(ref _launchGeneration));
        Interlocked.Exchange(ref _launchInFlight, 0);
        Interlocked.Increment(ref _launchGeneration);
    }

    private static void EnsureWorldReadyWatch()
    {
        if (_worldReadySub != null) return;
        try
        {
            _worldReadySub = EventBus.On<WorldReady>(OnClientWorldReady, owner: typeof(ConnectOps));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"connect: WorldReady subscribe failed: {e.Message}");
        }
    }

    private static void OnClientWorldReady(WorldReady ev)
    {
        try
        {
            if (ev.IsServer) return;
            RestoreClientHostSettings(Volatile.Read(ref _launchGeneration));
            if (Interlocked.Exchange(ref _launchInFlight, 0) != 0)
                Plugin.Logger?.LogInfo("connect: launch-in-flight cleared (client world ready)");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"connect: WorldReady handler threw: {e}");
        }
    }

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        try { return el.GetString(); }
        catch { return null; }
    }

    /// <summary>
    /// <c>args.port</c> when the caller actually sent it. Null means "not present" so the
    /// address parser can take a port split from <c>host:port</c> instead of treating the
    /// old 9976 fallback as an explicit override.
    /// </summary>
    private static int? TryGetOptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
                return n;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                throw new ArgumentException($"args.{name} must be an integer (got '{s}')");
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch { }
        throw new ArgumentException($"args.{name} must be an integer");
    }

    private static bool GetBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;
        try
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var s))
                return s;
        }
        catch { }
        return fallback;
    }

    private static void ValidateSaveName(string saveName)
    {
        if (saveName.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            throw new ArgumentException("args.saveName must not contain path or wildcard characters");
        if (saveName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("args.saveName must not contain '..'");
        if (saveName.Length > 64)
            throw new ArgumentException("args.saveName is longer than 64 characters");
    }

    /// <summary>
    /// Menu defaults 9876 / 9877 / 9878. <c>args.port</c> shifts the trio so a test can
    /// use 9886 / 9887 / 9888 without colliding with the live dedicated server on 9876.
    /// </summary>
    private static (ushort HostPort, int QueryPort, ushort FallbackPort, bool FromArgs) ResolveHostPorts(JsonElement args)
    {
        int? requested = TryGetOptionalInt(args, "port");
        if (requested is not int p)
            return ((ushort)DefaultHostPort, DefaultQueryPort, (ushort)DefaultFallbackPort, false);

        if (p <= 0 || p > 65535)
            throw new ArgumentException($"args.port must be 1–65535 (got {p})");
        if (p + 2 > 65535)
            throw new ArgumentException($"args.port {p} leaves no room for query/fallback ports (+1/+2)");

        return ((ushort)p, p + 1, (ushort)(p + 2), true);
    }

    /// <summary>
    /// The live dedicated server on this machine binds UDP 9876. A hosted client that
    /// connects to <c>SteamIPv4://127.0.0.1:9876</c> would reach that process instead of
    /// the bundled child. Refuse the captured default when something is already listening.
    /// </summary>
    private static void RefuseIfDefaultPortBusy(ushort hostPort)
    {
        if (hostPort != DefaultHostPort)
            return;
        if (!UdpPortInUse(DefaultHostPort))
            return;
        throw new InvalidOperationException(
            "default host port 9876 is already in use (the live dedicated server on this machine also binds 9876). " +
            "Pass args.port (e.g. 9886) so the hosted game uses 9886/9887/9888.");
    }

    private static bool UdpPortInUse(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            if (listeners == null)
                return false;
            foreach (var ep in listeners)
            {
                if (ep != null && ep.Port == port)
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: UDP listener probe failed (not refusing default port): {e.Message}");
        }
        return false;
    }

    /// <summary>
    /// Live evidence §24: the child argv is
    /// <c>-gamePort {ClientHostSettings.Port} -queryPort {ClientHostSettings.QueryPort}
    /// -secure {ClientHostSettings.Secure} -lanMode {ServerHostSettings.LanMode}</c>.
    /// <see cref="GameConnect.HostPort"/> is the client's loopback connect port only; it does
    /// not feed <c>-gamePort</c>. <see cref="ClientHostSettings"/> has no FallbackPort and no
    /// LanMode (native <c>ProjectM.ClientHostSettings</c>: Port, QueryPort, Secure).
    /// </summary>
    /// <remarks>
    /// Values stay until <see cref="RestoreClientHostSettings"/> on client WorldReady, launch
    /// abort, the 120 s timeout, or Dev Unload. Restoring when <see cref="GameHelper.LaunchGame"/>
    /// returns would put the shipped 9876/9877 back before <c>GameBootstrap.Start</c> builds argv.
    /// Secure is left as the in-memory value (shipped default false; §21 capture and §24 argv
    /// both had <c>-secure False</c>); there is no <c>args.secure</c>.
    /// </remarks>
    private static ClientHostSnapshot ApplyClientHostSettings(ushort hostPort, int queryPort)
    {
        var snapshot = new ClientHostSnapshot();
        try
        {
            var host = SettingsManager.ClientSettings?.HostSettings;
            if (host == null)
            {
                Plugin.Logger?.LogWarning(
                    "host: SettingsManager.ClientSettings.HostSettings is null; " +
                    "child argv -gamePort/-queryPort may stay at shipped 9876/9877 (evidence §24).");
                return snapshot;
            }

            snapshot.Captured = true;
            snapshot.Port = host.Port;
            snapshot.QueryPort = host.QueryPort;
            snapshot.Secure = host.Secure;

            host.Port = hostPort;
            host.QueryPort = queryPort;
            // Secure is not args-driven and is not a GameConnect field. Leave it.

            _clientHostSnapshot = snapshot;
            _clientHostRestoreGeneration = Volatile.Read(ref _launchGeneration);

            Plugin.Logger?.LogInfo(
                $"host: ClientHostSettings Port {snapshot.Port} -> {host.Port}, " +
                $"QueryPort {snapshot.QueryPort} -> {host.QueryPort}, Secure={host.Secure} (unchanged; feeds argv -secure). " +
                "These stay until WorldReady: LaunchGame returns before GameBootstrap.Start builds the child argv.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ClientHostSettings update skipped: {e.Message}");
        }

        return snapshot;
    }

    /// <summary>
    /// Put the pre-launch <see cref="ClientHostSettings"/> Port/QueryPort back. No-op when
    /// nothing was captured or <paramref name="generation"/> is a stale launch. Does not write
    /// <c>ClientSettings.json</c> — the mutation was in-memory only.
    /// </summary>
    private static void RestoreClientHostSettings(int generation = -1)
    {
        try
        {
            var snapshot = _clientHostSnapshot;
            if (!snapshot.Captured) return;
            if (generation >= 0 && Volatile.Read(ref _clientHostRestoreGeneration) != generation)
                return;

            var host = SettingsManager.ClientSettings?.HostSettings;
            if (host == null)
            {
                Plugin.Logger?.LogWarning("host: ClientHostSettings restore skipped (HostSettings null).");
                return;
            }

            host.Port = snapshot.Port;
            host.QueryPort = snapshot.QueryPort;
            _clientHostSnapshot = default;
            Interlocked.Exchange(ref _clientHostRestoreGeneration, 0);
            Plugin.Logger?.LogInfo(
                $"host: restored ClientHostSettings Port={host.Port} QueryPort={host.QueryPort} Secure={host.Secure}.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ClientHostSettings restore skipped: {e.Message}");
        }
    }

    private static object DumpClientHostSettings(ClientHostSnapshot snapshot, ushort hostPort, int queryPort)
    {
        try
        {
            var host = SettingsManager.ClientSettings?.HostSettings;
            return new
            {
                present = host != null,
                captured = snapshot.Captured,
                previousPort = snapshot.Port,
                previousQueryPort = snapshot.QueryPort,
                previousSecure = snapshot.Secure,
                Port = host?.Port ?? (int)hostPort,
                QueryPort = host?.QueryPort ?? queryPort,
                Secure = host?.Secure ?? snapshot.Secure,
                restoreWhen = "client WorldReady / launch abort / 120s timeout / Dev Unload (not when LaunchGame returns)",
                argv = $"-gamePort {hostPort} -queryPort {queryPort} -secure {(host?.Secure ?? snapshot.Secure)}",
            };
        }
        catch (Exception e)
        {
            return new { present = false, dumpError = e.Message };
        }
    }

    /// <summary>
    /// LAN and the server password are not <see cref="GameConnect"/> fields and are not on the
    /// child-server argv template (except <c>-lanMode</c>, which GameBootstrap reads from
    /// <see cref="ServerHostSettings.LanMode"/>, not from <see cref="ClientHostSettings"/>).
    /// Poke the in-memory singleton, then persist it into the save folder so a
    /// reload-from-disk inside <c>GameBootstrap.Start</c> still sees the values.
    /// </summary>
    private static ServerHostSettings? ApplyInMemoryHostSettings(
        string saveName, string? password, bool lan, ushort hostPort, int queryPort, int hostMaxUsers, ushort hostFallbackPort)
    {
        ServerHostSettings? host = null;
        try
        {
            host = SettingsManager.ServerHostSettings;
            if (host == null)
            {
                Plugin.Logger?.LogWarning("host: SettingsManager.ServerHostSettings is null; LAN/password may not reach the child server.");
            }
            else
            {
                // Captured menu left Name="V Rising Server", Address=null, PrivateGame=False,
                // GameClient.IsPrivateGame=False. We still write SaveName/ports/password/LAN so
                // the child argv and save-folder JSON match this op's world; we do not invent
                // Address, PrivateGame, or IsPrivateGame.
                host.SaveName = saveName;
                host.Name = saveName;
                host.Password = password ?? "";
                host.LanMode = lan;
                host.Port = hostPort;
                host.QueryPort = queryPort;
                host.FallbackPort = hostFallbackPort;
                if (hostMaxUsers > 0)
                    host.MaxConnectedUsers = hostMaxUsers;
                host.ListOnSteam = false;
                host.ListOnEOS = false;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: in-memory ServerHostSettings update skipped: {e.Message}");
        }

        return host;
    }

    /// <summary>
    /// The captured menu left both preset names empty. Clear leftovers from a previous
    /// <c>host</c> in this process so Start does not look up a TextAsset we never applied.
    /// </summary>
    private static DifficultyApplyResult ClearDifficultyPresets(ServerHostSettings? host)
    {
        try
        {
            if (host != null)
            {
                host.GameDifficultyPreset = "";
                host.GameSettingsPreset = "";
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: clearing empty difficulty/game-settings presets skipped: {e.Message}");
        }

        return new DifficultyApplyResult
        {
            PresetName = "",
            Applied = false,
            Source = "capture: menu left both presets empty (pass args.difficulty to apply)",
            LogLine = "not applied (menu capture: GameDifficultyPreset=\"\" GameSettingsPreset=\"\")",
        };
    }

    /// <summary>
    /// Host New's view init is what calls <see cref="SettingsManager.VerifyServerGameSettings"/>
    /// on the client (that method has CallerCount 5; <c>VerifyServerSettings</c> has 103).
    /// Without it, <see cref="SettingsManager.ServerGameSettings"/> can be a default-constructed
    /// object whose nested modifier classes are still null.
    /// </summary>
    private static void EnsureSettingsSingletons()
    {
        try
        {
            SettingsManager.VerifyServerSettings(silentInitialize: false);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: VerifyServerSettings skipped: {e.Message}");
        }

        try
        {
            SettingsManager.VerifyServerGameSettings(silentInitialize: false);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: VerifyServerGameSettings skipped: {e.Message}");
        }

        try
        {
            if (SettingsManager.ServerHostSettings == null)
            {
                // Public getter is read-only (it Verify-lazy-inits). The backing field is the
                // assignable singleton Host New would leave populated.
                SettingsManager._ServerHostSettings = new ServerHostSettings(loadDefault: true);
                SettingsManager._InitializedServerSettings = true;
                Plugin.Logger?.LogInfo("host: constructed ServerHostSettings(loadDefault: true); manager singleton was null.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ServerHostSettings construct skipped: {e.Message}");
        }

        try
        {
            if (SettingsManager.ServerGameSettings == null)
            {
                SettingsManager._ServerGameSettings = new ServerGameSettings(loadDefault: true);
                SettingsManager._InitializedServerGameSettings = true;
                Plugin.Logger?.LogInfo("host: constructed ServerGameSettings(loadDefault: true); manager singleton was null.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ServerGameSettings construct skipped: {e.Message}");
        }
    }

    /// <summary>
    /// Default <c>ServerHostSettings.json</c> ships <c>Rcon: {}</c> but no <c>API</c> object.
    /// An existing private save writes both as empty objects. Host New therefore materializes
    /// <see cref="WebApiSettings"/> before LaunchGame; Start may dereference them.
    /// </summary>
    private static void EnsureNestedHostObjects(ServerHostSettings? host)
    {
        if (host == null) return;
        try
        {
            if (host.API == null)
            {
                host.API = new WebApiSettings();
                Plugin.Logger?.LogInfo("host: ServerHostSettings.API was null; constructed WebApiSettings().");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: WebApiSettings construct skipped: {e.Message}");
        }

        try
        {
            if (host.Rcon == null)
            {
                host.Rcon = new RconSettings();
                Plugin.Logger?.LogInfo("host: ServerHostSettings.Rcon was null; constructed RconSettings().");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: RconSettings construct skipped: {e.Message}");
        }
    }

    /// <summary>
    /// Port of <c>BasicServerSettingsPanel.ApplyDifficultySettings(TextAsset)</c>: overlay a
    /// difficulty JSON onto <see cref="SettingsManager.ServerGameSettings"/> and set
    /// <see cref="ServerHostSettings.GameDifficultyPreset"/> to the TextAsset / file name
    /// <see cref="GameBootstrap.Start"/> looks up in <c>DifficultySettingPresets</c>.
    /// </summary>
    private static DifficultyApplyResult ApplyDifficultyPreset(ServerHostSettings? host, string? requested)
    {
        var result = new DifficultyApplyResult
        {
            PresetName = DefaultDifficultyPresetName,
            Applied = false,
            Source = "none",
            LogLine = "not applied",
        };

        var mapped = MapDifficultyArg(requested);
        result.PresetName = mapped.PresetName;

        try
        {
            var game = SettingsManager.ServerGameSettings;
            if (game == null)
            {
                result.LogLine = "ServerGameSettings still null after EnsureSettingsSingletons";
                Plugin.Logger?.LogWarning($"host: {result.LogLine}");
                return result;
            }

            string? json = null;
            string source = "none";

            // Prefer the same TextAsset list Host New reads, if a GameBootstrap is already in
            // the menu / boot scene. Fall back to the StreamingAssets JSON those assets wrap.
            json = TryReadDifficultyTextAsset(mapped.PresetName);
            if (json != null)
            {
                source = "DifficultySettingPresets TextAsset";
            }
            else
            {
                var path = ResolveDifficultyPresetPath(mapped.PresetName);
                result.FilePath = path;
                if (path != null && File.Exists(path))
                {
                    json = File.ReadAllText(path);
                    source = "StreamingAssets file";
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                // Last resort: construct a loadDefault settings object so nested modifier
                // classes exist even if we could not find the preset JSON.
                try
                {
                    var fallback = new ServerGameSettings(loadDefault: true);
                    game.CopyDifficultySettings(fallback);
                    game.GameDifficulty = mapped.Difficulty;
                    source = "ServerGameSettings(loadDefault) copy";
                    Plugin.Logger?.LogWarning(
                        $"host: difficulty JSON for {mapped.PresetName} not found; copied loadDefault settings.");
                }
                catch (Exception e)
                {
                    result.LogLine = $"no JSON and loadDefault copy failed: {e.Message}";
                    Plugin.Logger?.LogWarning($"host: {result.LogLine}");
                    return result;
                }
            }
            else
            {
                game.PopulateFromJson(json);
                game.GameDifficulty = mapped.Difficulty;
            }

            if (host != null)
                host.GameDifficultyPreset = mapped.PresetName;

            result.Applied = true;
            result.Source = source;
            result.GameDifficulty = mapped.Difficulty.ToString();
            bool modifiersPresent = false;
            try { modifiersPresent = game.UnitStatModifiers_Global != null; }
            catch { }
            result.UnitStatModifiersGlobalPresent = modifiersPresent;
            result.LogLine =
                $"preset={mapped.PresetName} difficulty={mapped.Difficulty} source={source} " +
                $"UnitStatModifiers_Global={(modifiersPresent ? "set" : "null")}";
            return result;
        }
        catch (Exception e)
        {
            result.LogLine = "ApplyDifficultyPreset threw: " + e.Message;
            Plugin.Logger?.LogWarning($"host: ApplyDifficultyPreset skipped: {e}");
            return result;
        }
    }

    private static (string PresetName, GameDifficulty Difficulty) MapDifficultyArg(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return (DefaultDifficultyPresetName, GameDifficulty.Normal);

        switch (requested.Trim().ToLowerInvariant())
        {
            case "easy":
            case "relaxed":
            case "difficulty_easy":
                return ("Difficulty_Easy", GameDifficulty.Relaxed);
            case "hard":
            case "brutal":
            case "difficulty_brutal":
                return ("Difficulty_Brutal", GameDifficulty.Hard);
            case "normal":
            case "difficulty_normal":
                return (DefaultDifficultyPresetName, GameDifficulty.Normal);
            default:
                Plugin.Logger?.LogWarning($"host: unknown args.difficulty '{requested}', using {DefaultDifficultyPresetName}.");
                return (DefaultDifficultyPresetName, GameDifficulty.Normal);
        }
    }

    private static string? TryReadDifficultyTextAsset(string presetName)
    {
        try
        {
            var bootstrap = UnityObject.FindObjectOfType<GameBootstrap>();
            var list = bootstrap?.DifficultySettingPresets;
            if (list == null) return null;
            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                TextAsset? asset = null;
                try { asset = list[i]; }
                catch { continue; }
                if (asset == null) continue;
                string? name = null;
                try { name = asset.name; }
                catch { }
                if (!string.Equals(name, presetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var text = asset.text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Plugin.Logger?.LogInfo($"host: difficulty TextAsset '{name}' from GameBootstrap.DifficultySettingPresets[{i}].");
                        return text;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"host: DifficultySettingPresets[{i}].text unreadable: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: GameBootstrap.DifficultySettingPresets unreadable: {e.Message}");
        }

        return null;
    }

    private static string? ResolveDifficultyPresetPath(string presetName)
    {
        var file = presetName + ".json";
        try
        {
            var streaming = Application.streamingAssetsPath;
            if (!string.IsNullOrWhiteSpace(streaming))
            {
                var p = Path.GetFullPath(Path.Combine(streaming, "GameDifficultyPresets", file));
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: Application.streamingAssetsPath unreadable: {e.Message}");
        }

        try
        {
            var def = ServerGameSettings.DefaultPath;
            if (!string.IsNullOrWhiteSpace(def))
            {
                var settingsDir = Path.GetDirectoryName(Path.GetFullPath(def));
                var streaming = settingsDir != null ? Path.GetDirectoryName(settingsDir) : null;
                if (streaming != null)
                {
                    var p = Path.GetFullPath(Path.Combine(streaming, "GameDifficultyPresets", file));
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ServerGameSettings.DefaultPath walk skipped: {e.Message}");
        }

        return null;
    }

    private static string ReadPersistentDataPath()
    {
        try
        {
            var path = StunApplication.persistentDataPath;
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("StunApplication.persistentDataPath is empty");
            return Path.GetFullPath(path);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("could not read StunApplication.persistentDataPath: " + e.Message, e);
        }
    }

    /// <summary>
    /// Hosted private games resolve to <c>{persistentDataPath}\Saves\v4\{saveName}</c>
    /// (verified live: GameBootstrap.GetServerSaveSettings logged that exact path).
    /// <see cref="SavePaths.FindSaveFolder"/> already builds that when the folder is missing.
    /// </summary>
    private static string ResolveHostSaveDirectory(string persistRoot, string saveName)
    {
        var resolved = Path.GetFullPath(SavePaths.FindSaveFolder(persistRoot, saveName));
        var rootFull = Path.GetFullPath(persistRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = rootFull + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolved, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"host save directory '{resolved}' is outside persistentDataPath '{persistRoot}'");
        }
        return resolved;
    }

    /// <summary>
    /// GameBootstrap.Start NREs when this folder is missing (Player.log 2026-09-02 host op).
    /// Host New creates it before LaunchGame; an empty folder is enough for
    /// TryFindMostRecentSave to treat it as a first-time world rather than a missing path.
    /// </summary>
    private static bool EnsureHostSaveDirectory(string saveDirectory, string persistRoot)
    {
        if (Directory.Exists(saveDirectory))
            return false;
        Directory.CreateDirectory(saveDirectory);
        Plugin.Logger?.LogInfo($"host: created save directory '{saveDirectory}' under '{persistRoot}'.");
        return true;
    }

    private static string? EnsureSessionIdFile(string saveDirectory)
    {
        try
        {
            string fileName = "SessionId.json";
            try
            {
                var fromGame = SaveFileData.SessionIdFileName;
                if (!string.IsNullOrWhiteSpace(fromGame))
                    fileName = fromGame;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"host: SaveFileData.SessionIdFileName unreadable, using {fileName}: {e.Message}");
            }

            var path = Path.Combine(saveDirectory, fileName);
            if (!File.Exists(path))
            {
                var id = Guid.NewGuid().ToString();
                File.WriteAllText(path, id);
                Plugin.Logger?.LogInfo($"host: wrote {fileName} ({id}).");
                return id;
            }

            return File.ReadAllText(path).Trim();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: SessionId file skipped: {e.Message}");
            return null;
        }
    }

    private static string? EnsureStartDateFile(string saveDirectory)
    {
        try
        {
            string fileName = "StartDate.json";
            try
            {
                var fromGame = SaveFileData.StartDateFileName;
                if (!string.IsNullOrWhiteSpace(fromGame))
                    fileName = fromGame;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"host: SaveFileData.StartDateFileName unreadable, using {fileName}: {e.Message}");
            }

            var path = Path.Combine(saveDirectory, fileName);
            if (!File.Exists(path))
            {
                var stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                File.WriteAllText(path, stamp);
                Plugin.Logger?.LogInfo($"host: wrote {fileName} ({stamp}).");
                return stamp;
            }

            return File.ReadAllText(path).Trim();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: StartDate file skipped: {e.Message}");
            return null;
        }
    }

    private static (string? HostSettingsPath, string? GameSettingsPath) PersistSettingsIntoSaveFolder(
        string saveDirectory, ServerHostSettings? host)
    {
        string? hostPath = null;
        string? gamePath = null;
        try
        {
            if (host != null)
            {
                hostPath = Path.Combine(saveDirectory, "ServerHostSettings.json");
                host.SaveToPath(hostPath, removeDefaultValues: true, saveWholeFile: false);
                Plugin.Logger?.LogInfo($"host: SaveToPath ServerHostSettings -> {hostPath}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ServerHostSettings.SaveToPath skipped: {e.Message}");
            hostPath = null;
        }

        try
        {
            var game = SettingsManager.ServerGameSettings;
            if (game != null)
            {
                gamePath = Path.Combine(saveDirectory, "ServerGameSettings.json");
                // Host New writes a full ServerGameSettings dump (existing private saves
                // have every nested modifier object). removeDefaultValues would drop
                // UnitStatModifiers matching 1.0 and Start would reload them as null.
                game.SaveToPath(gamePath, removeDefaultValues: false, saveWholeFile: true);
                Plugin.Logger?.LogInfo($"host: SaveToPath ServerGameSettings (whole) -> {gamePath}");
            }
            else
            {
                Plugin.Logger?.LogWarning("host: SettingsManager.ServerGameSettings is null; not writing ServerGameSettings.json.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ServerGameSettings.SaveToPath skipped: {e.Message}");
            gamePath = null;
        }

        return (hostPath, gamePath);
    }

    /// <summary>
    /// Snapshot of every object <c>GameBootstrap.Start</c> is inferred to dereference on the
    /// host branch, taken just before LaunchGame. Logged so the next live run can name the
    /// remaining null if Start still throws.
    /// </summary>
    private static NullProbe ProbeStartNulls(ServerHostSettings? host)
    {
        var probe = new NullProbe();
        try
        {
            probe.ServerHostSettingsPresent = host != null;
            if (host != null)
            {
                try { probe.GameDifficultyPreset = host.GameDifficultyPreset ?? "(null)"; }
                catch (Exception e) { probe.GameDifficultyPreset = "(unreadable: " + e.Message + ")"; }
                try { probe.GameSettingsPreset = host.GameSettingsPreset ?? "(null)"; }
                catch (Exception e) { probe.GameSettingsPreset = "(unreadable: " + e.Message + ")"; }
                try { probe.ApiPresent = host.API != null; }
                catch (Exception e) { probe.ApiPresent = false; probe.ApiError = e.Message; }
                try { probe.RconPresent = host.Rcon != null; }
                catch (Exception e) { probe.RconPresent = false; probe.RconError = e.Message; }
            }
        }
        catch (Exception e)
        {
            probe.HostError = e.Message;
        }

        try
        {
            var game = SettingsManager.ServerGameSettings;
            probe.ServerGameSettingsPresent = game != null;
            if (game != null)
            {
                try { probe.GameDifficulty = game.GameDifficulty.ToString(); }
                catch (Exception e) { probe.GameDifficulty = "(unreadable: " + e.Message + ")"; }
                try { probe.UnitStatModifiersGlobalPresent = game.UnitStatModifiers_Global != null; }
                catch (Exception e) { probe.UnitStatModifiersGlobalError = e.Message; }
                try { probe.UnitStatModifiersVBloodPresent = game.UnitStatModifiers_VBlood != null; }
                catch (Exception e) { probe.UnitStatModifiersVBloodError = e.Message; }
                try { probe.GameTimeModifiersPresent = game.GameTimeModifiers != null; }
                catch (Exception e) { probe.GameTimeModifiersError = e.Message; }
                try { probe.VampireStatModifiersPresent = game.VampireStatModifiers != null; }
                catch (Exception e) { probe.VampireStatModifiersError = e.Message; }
            }
        }
        catch (Exception e)
        {
            probe.GameSettingsError = e.Message;
        }

        try
        {
            var general = SettingsManager.ClientSettings?.General;
            probe.ClientGeneralPresent = general != null;
            probe.ServerPath = general?.ServerPath ?? "(null)";
        }
        catch (Exception e)
        {
            probe.ServerPath = "(unreadable: " + e.Message + ")";
        }

        try
        {
            var bootstrap = UnityObject.FindObjectOfType<GameBootstrap>();
            probe.GameBootstrapPresent = bootstrap != null;
            if (bootstrap != null)
            {
                try { probe.ServerHostDataPresent = bootstrap.ServerHostData != null; }
                catch (Exception e) { probe.ServerHostDataError = e.Message; }
                try { probe.ClientCreateDataPresent = bootstrap.ClientCreateData != null; }
                catch (Exception e) { probe.ClientCreateDataError = e.Message; }
                try
                {
                    var list = bootstrap.DifficultySettingPresets;
                    probe.DifficultySettingPresetsCount = list != null ? list.Count : -1;
                }
                catch (Exception e)
                {
                    probe.DifficultySettingPresetsError = e.Message;
                }

                // Menu-time GameBootstrap is the boot/menu instance. The game-scene instance
                // Start() actually runs on may be a different object; filling ServerHostData
                // here only helps if they are the same.
                try
                {
                    if (bootstrap.ServerHostData == null)
                    {
                        bootstrap.ServerHostData = new ServerHostData();
                        probe.ServerHostDataConstructed = true;
                        Plugin.Logger?.LogInfo("host: GameBootstrap.ServerHostData was null; constructed ServerHostData() on the menu-time instance.");
                    }
                }
                catch (Exception e)
                {
                    probe.ServerHostDataError = e.Message;
                }

                try
                {
                    if (bootstrap.ClientCreateData == null)
                    {
                        bootstrap.ClientCreateData = new ClientCreateData();
                        probe.ClientCreateDataConstructed = true;
                        Plugin.Logger?.LogInfo("host: GameBootstrap.ClientCreateData was null; constructed ClientCreateData() on the menu-time instance.");
                    }
                }
                catch (Exception e)
                {
                    probe.ClientCreateDataError = e.Message;
                }
            }
        }
        catch (Exception e)
        {
            probe.GameBootstrapError = e.Message;
        }

        probe.LogLine =
            $"hostSettings={probe.ServerHostSettingsPresent} " +
            $"gameSettings={probe.ServerGameSettingsPresent} " +
            $"GameDifficultyPreset={probe.GameDifficultyPreset ?? "(unset)"} " +
            $"API={probe.ApiPresent} Rcon={probe.RconPresent} " +
            $"UnitStatModifiers_Global={probe.UnitStatModifiersGlobalPresent} " +
            $"GameTimeModifiers={probe.GameTimeModifiersPresent} " +
            $"ServerPath={probe.ServerPath} " +
            $"GameBootstrap={probe.GameBootstrapPresent} " +
            $"ServerHostData={probe.ServerHostDataPresent} " +
            $"DifficultySettingPresets={probe.DifficultySettingPresetsCount}";
        return probe;
    }

    private static object DumpGameConnect(
        GameConnect connect, string saveName, bool passwordSet, StunAnalytics.Client.ServerJoinMethod joinMethod)
    {
        // Do not call ConnectAddress.ToString() — the interop wrapper is a ValueType with
        // reference fields (trap 3). Format the captured URL shapes from the ports we set.
        string hostAddress;
        try { hostAddress = connect.HostAddress.ToString(); }
        catch { hostAddress = ""; }
        string saveFile;
        try { saveFile = connect.SaveFile.ToString(); }
        catch { saveFile = ""; }

        return new
        {
            ShouldHost = connect.ShouldHost,
            CreateClient = connect.CreateClient,
            HostDedicated = connect.HostDedicated,
            PlayReplay = connect.PlayReplay,
            SavePerUser = connect.SavePerUser,
            HostPort = connect.HostPort,
            HostFallbackPort = connect.HostFallbackPort,
            HostMaxUsers = connect.HostMaxUsers,
            HostAddress = hostAddress,
            ServerSaveName = saveName,
            SaveFile = saveFile,
            ConnectAddress = $"SteamIPv4://127.0.0.1:{connect.HostPort}",
            FallbackConnectAddress = $"Lidgren://127.0.0.1:{connect.HostFallbackPort}",
            ConnectPasswordSet = passwordSet,
            AnalyticsJoinMethod = joinMethod.ToString(),
        };
    }

    private static object DumpHostSettings(
        ServerHostSettings? host,
        string saveName,
        bool passwordSet,
        bool lan,
        ushort hostPort,
        int queryPort,
        int hostMaxUsers,
        ushort hostFallbackPort)
    {
        if (host == null)
        {
            return new
            {
                present = false,
                SaveName = saveName,
                Name = saveName,
                PasswordSet = passwordSet,
                LanMode = lan,
                Port = (int)hostPort,
                QueryPort = queryPort,
                FallbackPort = (int)hostFallbackPort,
                MaxConnectedUsers = hostMaxUsers,
                ListOnSteam = false,
                ListOnEOS = false,
            };
        }

        try
        {
            return new
            {
                present = true,
                SaveName = host.SaveName,
                Name = host.Name,
                PasswordSet = !string.IsNullOrEmpty(host.Password),
                LanMode = host.LanMode,
                PrivateGame = host.PrivateGame,
                Port = host.Port,
                QueryPort = host.QueryPort,
                FallbackPort = host.FallbackPort,
                MaxConnectedUsers = host.MaxConnectedUsers,
                ListOnSteam = host.ListOnSteam,
                ListOnEOS = host.ListOnEOS,
                Address = host.Address,
                GameSettingsPreset = host.GameSettingsPreset,
                GameDifficultyPreset = host.GameDifficultyPreset,
                ApiPresent = host.API != null,
                RconPresent = host.Rcon != null,
            };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"host: ServerHostSettings dump partial: {e.Message}");
            return new
            {
                present = true,
                SaveName = saveName,
                dumpError = e.Message,
            };
        }
    }

    /// <summary>Result of overlaying a Host New difficulty preset onto ServerGameSettings.</summary>
    private sealed class DifficultyApplyResult
    {
        public string PresetName { get; set; } = DefaultDifficultyPresetName;
        public bool Applied { get; set; }
        public string Source { get; set; } = "none";
        public string? FilePath { get; set; }
        public string? GameDifficulty { get; set; }
        public bool UnitStatModifiersGlobalPresent { get; set; }
        public string LogLine { get; set; } = "";
    }

    /// <summary>
    /// Pre-LaunchGame snapshot of every object Start is inferred to touch on the host branch.
    /// Serialized into the bridge response so a later NRE can be matched to a remaining null.
    /// </summary>
    private sealed class NullProbe
    {
        public bool ServerHostSettingsPresent { get; set; }
        public string? GameDifficultyPreset { get; set; }
        public string? GameSettingsPreset { get; set; }
        public bool ApiPresent { get; set; }
        public string? ApiError { get; set; }
        public bool RconPresent { get; set; }
        public string? RconError { get; set; }
        public string? HostError { get; set; }
        public bool ServerGameSettingsPresent { get; set; }
        public string? GameDifficulty { get; set; }
        public bool UnitStatModifiersGlobalPresent { get; set; }
        public string? UnitStatModifiersGlobalError { get; set; }
        public bool UnitStatModifiersVBloodPresent { get; set; }
        public string? UnitStatModifiersVBloodError { get; set; }
        public bool GameTimeModifiersPresent { get; set; }
        public string? GameTimeModifiersError { get; set; }
        public bool VampireStatModifiersPresent { get; set; }
        public string? VampireStatModifiersError { get; set; }
        public string? GameSettingsError { get; set; }
        public bool ClientGeneralPresent { get; set; }
        public string? ServerPath { get; set; }
        public bool GameBootstrapPresent { get; set; }
        public string? GameBootstrapError { get; set; }
        public bool ServerHostDataPresent { get; set; }
        public bool ServerHostDataConstructed { get; set; }
        public string? ServerHostDataError { get; set; }
        public bool ClientCreateDataPresent { get; set; }
        public bool ClientCreateDataConstructed { get; set; }
        public string? ClientCreateDataError { get; set; }
        public int DifficultySettingPresetsCount { get; set; }
        public string? DifficultySettingPresetsError { get; set; }
        public string LogLine { get; set; } = "";
    }

    /// <summary>
    /// Pre-mutation <see cref="ClientHostSettings"/> values. Port/QueryPort are restored after
    /// the child argv is built; Secure is captured only for the log (we do not write it).
    /// </summary>
    private struct ClientHostSnapshot
    {
        public bool Captured;
        public int Port;
        public int QueryPort;
        public bool Secure;
    }
#endif
}
