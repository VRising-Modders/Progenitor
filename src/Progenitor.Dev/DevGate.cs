using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Progenitor.Networking;

namespace Progenitor.Dev;

/// <summary>Where the opt-in that allowed the bridge to start came from.</summary>
internal enum DevOptInSource
{
    /// <summary>No opt-in was found; the bridge must not bind.</summary>
    None = 0,

    /// <summary>The marker file <c>BepInEx/config/progenitor-dev.enable</c>.</summary>
    Marker = 1,

    /// <summary>The environment variable <c>PROGENITOR_DEV=1</c>.</summary>
    Env = 2,
}

/// <summary>
/// What the gate could learn about a dedicated-server process before the game's own settings
/// singleton is populated. Every field is optional: <see cref="SettingsFound"/> false means the
/// server's <c>ServerHostSettings.json</c> was not located, not that the server is private.
/// </summary>
internal sealed class ServerExposure
{
    /// <summary>
    /// True when a <c>ServerHostSettings.json</c> was read <b>and</b> it carried the keys the gate
    /// decides on (<see cref="DevGate.RequiredSettingsKeys"/>). R28 finding 10: returning true for
    /// any parseable JSON object inverted the file's own policy — settings <i>missing</i> failed
    /// closed while settings <i>present but uninformative</i> failed open, and a stray
    /// <c>{"Name":"x"}</c> in the game root satisfied the gate on a server listed on Steam.
    /// </summary>
    public bool SettingsFound { get; set; }

    /// <summary>
    /// A settings file parsed but did not carry the keys the gate needs. Distinct from "no file":
    /// it says the gate looked at something and learned nothing, which belongs in the refusal text.
    /// </summary>
    public bool SettingsIncomplete { get; set; }

    /// <summary>Full path of the settings file that was read, or the paths that were tried.</summary>
    public string SettingsPath { get; set; } = "";

    /// <summary>
    /// Explicit bind address if the operator configured one (<c>-address</c> on the command line
    /// or an <c>"Address"</c> key in <c>ServerHostSettings.json</c>). Stock V Rising has no such
    /// key and binds every interface, so this is normally null.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>Game port (<c>ServerHostSettings.Port</c> or <c>-gamePort</c>). Informational.</summary>
    public int? Port { get; set; }

    /// <summary>
    /// <c>ServerHostSettings.ListOnSteam</c>: the Steam master-server listing flag. True means
    /// strangers can find this server, which is the "public bind" signal the gate refuses on.
    /// </summary>
    public bool? ListOnSteam { get; set; }

    /// <summary><c>ServerHostSettings.ListOnEOS</c>: the Epic master-server listing flag.</summary>
    public bool? ListOnEOS { get; set; }

    /// <summary><c>ServerHostSettings.LanMode</c>. Informational; a private-instance hint only.</summary>
    public bool? LanMode { get; set; }

    /// <summary><c>ServerHostSettings.PrivateGame</c>. Informational; a private-instance hint only.</summary>
    public bool? PrivateGame { get; set; }

    /// <summary>Persistent-data / save folder for this process, when known.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Full path of <c>VRisingServer.exe</c>, when known.</summary>
    public string? ProcessPath { get; set; }

    /// <summary>Process command line, when known.</summary>
    public string? CommandLine { get; set; }
}

/// <summary>Everything <see cref="DevGate.Evaluate"/> needs. Deliberately free of game types.</summary>
internal sealed class DevGateInputs
{
    /// <summary>Path the marker file was looked for at (used in the refusal text).</summary>
    public string MarkerPath { get; set; } = "";

    /// <summary>True when a file exists at <see cref="MarkerPath"/>.</summary>
    public bool MarkerExists { get; set; }

    /// <summary>Contents of the marker file, or null when it does not exist / could not be read.</summary>
    public string? MarkerText { get; set; }

    /// <summary>Raw value of the <c>PROGENITOR_DEV</c> environment variable.</summary>
    public string? EnvValue { get; set; }

    /// <summary>True in the dedicated-server process.</summary>
    public bool IsServerProcess { get; set; }

    /// <summary>Address the listener would bind. Anything but loopback is refused.</summary>
    public string BindAddress { get; set; } = DevGate.LoopbackAddress;

    /// <summary>Port the listener would bind. Reported in the log line only.</summary>
    public int Port { get; set; }

    /// <summary>Server exposure facts. Ignored when <see cref="IsServerProcess"/> is false.</summary>
    public ServerExposure Server { get; set; } = new ServerExposure();
}

/// <summary>Result of one gate evaluation.</summary>
internal sealed class DevGateDecision
{
    /// <summary>True when the bridge may bind.</summary>
    public bool Allowed { get; set; }

    /// <summary>Which opt-in was accepted. <see cref="DevOptInSource.None"/> when refused for lack of one.</summary>
    public DevOptInSource OptIn { get; set; }

    /// <summary>Why the bridge was refused. Empty when <see cref="Allowed"/>.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Extra line to log when allowed on a server process. Null when there is nothing to say.</summary>
    public string? Warning { get; set; }

    /// <summary><c>marker</c>, <c>env</c> or <c>none</c> — the token used in the startup log line.</summary>
    public string OptInLabel =>
        OptIn == DevOptInSource.Marker ? "marker" :
        OptIn == DevOptInSource.Env ? "env" : "none";
}

/// <summary>
/// Runtime refusal for the debug bridge (threat model surface 10). The Dev DLL is a full remote
/// control of the game process for anything that can read the token file, so it must never start
/// by accident on somebody's real server. Three conditions, all required:
///
/// <list type="number">
/// <item>an explicit opt-in: the marker file <c>BepInEx/config/progenitor-dev.enable</c> containing
/// <see cref="MarkerText"/>, or <c>PROGENITOR_DEV=1</c> in the environment;</item>
/// <item>on a dedicated server, the process positively identifies itself as one of ours. The
/// decision table is in docs/bridge.md and in <see cref="Evaluate"/>: the live server root is
/// refused unconditionally; a listed server or an explicit non-loopback bind is refused; and a
/// server that is neither a <c>_testserver</c> instance nor a <c>progenitor-hosted</c> child is
/// refused whatever its settings say, because <b>not listed is not private</b> (R28 finding 19) and
/// an uninformative settings file is not consent (R28 finding 10);</item>
/// <item>the listener binds loopback, and the token file is regenerated each start and written with
/// an owner-only ACL that is read back to confirm it (R28 findings 20 and 24).</item>
/// </list>
///
/// This file must stay free of BepInEx and game types: the test project compiles it directly.
/// </summary>
internal static class DevGate
{
    /// <summary>Marker file name, resolved against <c>BepInEx/config</c>.</summary>
    internal const string MarkerFileName = "progenitor-dev.enable";

    /// <summary>
    /// The literal sentence the marker file must contain. Any other content (comments, a date, a
    /// reason) may sit around it; the gate looks for this line anywhere in the file.
    /// </summary>
    internal const string MarkerText = "I understand this exposes a local control socket";

    /// <summary>Environment opt-in. Only the exact value <c>1</c> counts.</summary>
    internal const string EnvVarName = "PROGENITOR_DEV";

    /// <summary>Value <see cref="EnvVarName"/> must have.</summary>
    internal const string EnvVarValue = "1";

    /// <summary>The only address the bridge is allowed to bind.</summary>
    internal const string LoopbackAddress = "127.0.0.1";

    /// <summary>Token-file ACL state: read back and confirmed to grant only the current user.</summary>
    internal const string TokenAclRestricted = "restricted";

    /// <summary>Token-file ACL state: the file kept whatever the folder gives it.</summary>
    internal const string TokenAclDefault = "default";

    /// <summary>Settings file the dedicated server reads its listing flags and port from.</summary>
    internal const string ServerHostSettingsFileName = "ServerHostSettings.json";

    /// <summary>Token-file ACL state: the tightening ran but could not be read back to confirm it.</summary>
    internal const string TokenAclUnverified = "unverified";

    /// <summary>
    /// The keys a <c>ServerHostSettings.json</c> must actually carry before the gate treats it as
    /// having been read: a <c>Port</c>, and at least one of <c>ListOnSteam</c> / <c>ListOnEOS</c>.
    /// Anything else is a file the gate learned nothing from (R28 finding 10).
    /// </summary>
    internal const string RequiredSettingsKeys = "Port and ListOnSteam/ListOnEOS";

    private const string TestServerSegment = "_testserver";
    private const string HostedSaveToken = "progenitor-hosted";

    /// <summary>
    /// The user's live dedicated server. Duplicated from <c>HostedPathRules</c> on purpose: this
    /// file is compiled into the test project, which must not reference Core or the game
    /// (<c>ForbidGameReferences</c>). <c>ExtraInstanceServerAuth</c> and <c>WorldSaveOps</c> already
    /// fence on it; <c>DevGate</c> did not (R28 finding 19).
    /// </summary>
    internal const string LiveServerRoot = @"C:\VRisingServer";

    /// <summary>The one line to log when the bridge is allowed to bind.</summary>
    internal static string EnabledLine(DevGateDecision decision, string bindAddress, int port, string tokenAcl)
        => $"Dev bridge: enabled (opt-in={decision.OptInLabel}, bind={bindAddress}:{port}, token acl={tokenAcl})";

    /// <summary>The one line to log when the bridge refuses to bind.</summary>
    internal static string RefusedLine(DevGateDecision decision)
        => $"Dev bridge: refused ({decision.Reason})";

    /// <summary>
    /// How an operator opts in, for the refusal log line.
    ///
    /// <para>It deliberately does <b>not</b> print <see cref="MarkerText"/>. R28 finding 28.17: the
    /// refusal line published the exact sentence a marker file needs, in a log any third-party
    /// plugin in the same BepInEx install can read — the log told the attacker how to plant the
    /// opt-in. The sentence lives in docs/bridge.md, which is where someone entitled to enable the
    /// bridge is reading anyway.</para>
    /// </summary>
    internal static string MarkerHelp(string markerPath)
        => $"create {markerPath} with the opt-in line documented in docs/bridge.md (\"The opt-in marker\"), " +
           $"or set {EnvVarName}={EnvVarValue}";

    /// <summary>
    /// The whole policy. Pure: no disk, no environment, no game. Order of the checks is the order
    /// of the refusal reasons, so the most actionable one wins.
    /// </summary>
    internal static DevGateDecision Evaluate(DevGateInputs inputs)
    {
        var decision = new DevGateDecision();
        if (inputs == null)
        {
            decision.Reason = "no inputs";
            return decision;
        }

        // (a) explicit opt-in.
        var optIn = ResolveOptIn(inputs, out var optInReason);
        if (optIn == DevOptInSource.None)
        {
            decision.Reason = optInReason;
            return decision;
        }

        decision.OptIn = optIn;

        // (c) loopback bind. The listener already binds IPAddress.Loopback; this refuses a build
        // or a config that ever changed that.
        if (!IsLoopback(inputs.BindAddress))
        {
            decision.Reason =
                $"bind address {Describe(inputs.BindAddress)} is not loopback; the bridge binds {LoopbackAddress} only";
            return decision;
        }

        // (b) a dedicated server has to positively identify itself as one of ours.
        //
        // The decision table (docs/bridge.md "The opt-in marker"):
        //
        //   live server root under C:\VRisingServer .................. REFUSE, always
        //   ListOnSteam=true or ListOnEOS=true ....................... REFUSE (advertised)
        //   explicit non-loopback Address ............................ REFUSE (deliberate public bind)
        //   not a private/loopback/test instance ..................... REFUSE (see below)
        //   otherwise ................................................ ALLOW, with a warning
        //
        // R28 finding 19: "listed" is not "public". ListOnSteam=false / ListOnEOS=false on a V
        // Rising dedicated server means "not advertised", not "not reachable" — a whitelisted or
        // password-protected community server on a public IP has exactly those values. And the
        // private-instance test used to be conjoined with `!SettingsFound`, so it was skipped the
        // moment any settings file parsed: an internet-facing server never had to look like a
        // Progenitor instance at all. The identity test is now an independent requirement on every
        // server process, and finding a settings file waives nothing.
        if (inputs.IsServerProcess)
        {
            var server = inputs.Server ?? new ServerExposure();

            if (IsLiveServerPath(server))
            {
                decision.Reason =
                    $"this is the live dedicated server at {LiveServerRoot}; the debug bridge is never " +
                    "installed there";
                return decision;
            }

            if (server.ListOnSteam == true || server.ListOnEOS == true)
            {
                var which = server.ListOnSteam == true ? "ListOnSteam=true" : "ListOnEOS=true";
                decision.Reason =
                    $"dedicated server is listed on a master server ({ServerHostSettingsFileName} {which}); " +
                    "the debug bridge is full control of this process and never runs on a public server";
                return decision;
            }

            if (IsExplicitPublicAddress(server.Address))
            {
                decision.Reason =
                    $"dedicated server binds a non-loopback address (Address={server.Address}); " +
                    "the debug bridge never runs on a public server";
                return decision;
            }

            if (!HasPrivateInstanceSignal(server))
            {
                decision.Reason =
                    "server process that does not identify itself as a Progenitor test " +
                    $"({TestServerSegment}) or hosted ({HostedSaveToken}) instance " +
                    $"(-persistentDataPath / -saveName {Describe(server.SaveFolder)}, " +
                    $"process {Describe(server.ProcessPath)}); " +
                    (server.SettingsFound
                        ? $"{ServerHostSettingsFileName} says it is not listed, but not-listed is not " +
                          "private — a whitelisted server on a public IP reads the same"
                        : server.SettingsIncomplete
                            ? $"the {ServerHostSettingsFileName} at {Describe(server.SettingsPath)} carries " +
                              $"none of {RequiredSettingsKeys}, so nothing is known about it"
                            : $"no {ServerHostSettingsFileName} was found (looked at {Describe(server.SettingsPath)})");
                return decision;
            }

            decision.Warning = ServerWarning(server);
        }

        decision.Allowed = true;
        return decision;
    }

    /// <summary>
    /// Env wins over the marker so an extra instance can opt in without writing into the shared
    /// BepInEx config folder. A marker file whose text does not match is a refusal with its own
    /// reason: a half-written marker must not read as "no marker".
    /// </summary>
    private static DevOptInSource ResolveOptIn(DevGateInputs inputs, out string reason)
    {
        reason = "";
        var env = (inputs.EnvValue ?? "").Trim();
        if (env.Equals(EnvVarValue, StringComparison.Ordinal))
            return DevOptInSource.Env;

        if (inputs.MarkerExists)
        {
            if (MarkerTextMatches(inputs.MarkerText))
                return DevOptInSource.Marker;

            // Same reason as MarkerHelp: the log must not publish the sentence.
            reason =
                $"the opt-in marker {Describe(inputs.MarkerPath)} does not contain the opt-in line " +
                "documented in docs/bridge.md (\"The opt-in marker\")";
            return DevOptInSource.None;
        }

        reason = $"no opt-in; {MarkerHelp(Describe(inputs.MarkerPath))}";
        return DevOptInSource.None;
    }

    /// <summary>
    /// True when the marker file contains <see cref="MarkerText"/> on some line. Leading and
    /// trailing whitespace on that line is ignored; the sentence itself must be exact.
    /// </summary>
    internal static bool MarkerTextMatches(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.IndexOf(MarkerText, StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// True for an address an operator deliberately pointed at a network interface. Empty, a
    /// wildcard (<c>0.0.0.0</c>, <c>::</c>, <c>*</c>) and loopback are not: stock V Rising has no
    /// bind-address setting at all and listens on every interface, so a wildcard is the default
    /// rather than a decision, and the listing flags are what separate public from private.
    /// </summary>
    internal static bool IsExplicitPublicAddress(string? address)
    {
        var a = (address ?? "").Trim();
        if (a.Length == 0) return false;
        if (a == "*" || a == "0.0.0.0" || a == "::" || a == "[::]") return false;
        return !IsLoopback(a);
    }

    /// <summary>True for <c>127.0.0.0/8</c>, <c>::1</c>, and the literal name <c>localhost</c>.</summary>
    internal static bool IsLoopback(string? address)
    {
        var a = (address ?? "").Trim().Trim('[', ']');
        if (a.Length == 0) return false;
        if (a.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(a, out var parsed) && IPAddress.IsLoopback(parsed);
    }

    /// <summary>
    /// True when something about this process names it as one of ours: the staged test server
    /// (<c>_testserver</c> path segment) or a hosted child (<c>progenitor-hosted</c> save name).
    /// An independent requirement on every dedicated-server process, not a fallback for an
    /// unreadable settings file (R28 finding 19).
    ///
    /// <para>Only the <b>paths</b> count: the persistent-data path, the save name and the
    /// executable path. R28 finding 28.13: scanning the raw command line meant a bounded token
    /// anywhere in it qualified, so <c>-logFile "D:\logs\_testserver.log"</c> or
    /// <c>-serverName "progenitor-hosted"</c> flipped a stranger's server into the allowed branch.
    /// The two argv values that actually decide where a server's world lives are parsed out
    /// instead.</para>
    /// </summary>
    internal static bool HasPrivateInstanceSignal(ServerExposure server)
    {
        if (server == null) return false;
        var saveName = SaveNameOf(server);
        return ContainsPathSegment(server.SaveFolder, TestServerSegment)
            || ContainsPathSegment(server.ProcessPath, TestServerSegment)
            || ContainsPathSegment(server.SettingsPath, TestServerSegment)
            || ContainsBoundedToken(server.SaveFolder, HostedSaveToken)
            || ContainsBoundedToken(server.ProcessPath, HostedSaveToken)
            || ContainsBoundedToken(saveName, TestServerSegment)
            || ContainsBoundedToken(saveName, HostedSaveToken);
    }

    /// <summary>The <c>-saveName</c> argument, or empty. Never the whole command line.</summary>
    internal static string SaveNameOf(ServerExposure? server)
    {
        if (server == null) return "";
        return TryGetCommandArg(server.CommandLine, "-saveName", out var name) ? name : "";
    }

    /// <summary>
    /// True when any path this process is built from sits under the user's live dedicated server.
    /// Refused unconditionally, ahead of every other server test.
    /// </summary>
    internal static bool IsLiveServerPath(ServerExposure? server)
    {
        if (server == null) return false;
        return IsUnderLiveServerRoot(server.ProcessPath)
            || IsUnderLiveServerRoot(server.SaveFolder)
            || IsUnderLiveServerRoot(server.SettingsPath);
    }

    /// <summary>
    /// True when <paramref name="path"/> is <see cref="LiveServerRoot"/> or below it, after both
    /// sides are canonicalized by <see cref="LivePathFence"/>. See that type for the four forms
    /// that used to walk straight through this test (R29 finding 6).
    /// </summary>
    internal static bool IsUnderLiveServerRoot(string? path) =>
        LivePathFence.IsUnder(path, LiveServerRoot);

    private static string ServerWarning(ServerExposure server)
    {
        var sb = new StringBuilder();
        sb.Append("running on a dedicated-server process. Anything on this machine that can read the ");
        sb.Append("token file gets set/spawn/command/reload on this world. ");
        if (server.SettingsFound)
        {
            sb.Append(ServerHostSettingsFileName).Append(' ').Append(Describe(server.SettingsPath))
              .Append(": ListOnSteam=").Append(Text(server.ListOnSteam))
              .Append(", ListOnEOS=").Append(Text(server.ListOnEOS))
              .Append(", LanMode=").Append(Text(server.LanMode))
              .Append(", PrivateGame=").Append(Text(server.PrivateGame))
              .Append(", Port=").Append(server.Port.HasValue ? server.Port.Value.ToString(CultureInfo.InvariantCulture) : "unknown");
        }
        else
        {
            sb.Append(server.SettingsIncomplete ? "no usable " : "no ").Append(ServerHostSettingsFileName)
              .Append(" was read");
        }

        sb.Append(". Allowed because the path identifies a Progenitor test or hosted instance (")
          .Append(Describe(server.SaveFolder)).Append(')');
        return sb.ToString();
    }

    private static string Text(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "unknown";

    private static string Describe(string? value) => string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;

    // ---------------------------------------------------------------------------------------
    // Reading the server's own configuration.
    //
    // The dedicated server takes its persistent data folder from `-persistentDataPath` (see the
    // test server's start-test-server.bat and research/client-connect.md §argv) and reads
    // `<persistentDataPath>\Settings\ServerHostSettings.json`. The listing flags live there as
    // `ListOnSteam` / `ListOnEOS` (V Rising's equivalent of a "list on master server" flag);
    // `Port` / `QueryPort` are there too, with `-gamePort` / `-queryPort` overriding them. There
    // is no `Address` key in the stock file — the server binds every interface — so `Address`
    // and `-address` are read only because a wrapper or a future build may add them.
    //
    // This is read from disk rather than through `SettingsManager.ServerHostSettings` because the
    // gate runs in `Plugin.Load`, long before the game populates that singleton (the same reason
    // ExtraInstanceServerAuth documents its `Name` read as "may be null before settings load").
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Locate and parse the dedicated server's <c>ServerHostSettings.json</c>. Never throws.
    /// </summary>
    /// <param name="commandLine">Process command line (<c>-persistentDataPath</c>, <c>-address</c>, <c>-gamePort</c>).</param>
    /// <param name="processPath">Full path of the server executable, when known.</param>
    /// <param name="extraRoots">Additional folders to look under (BepInEx game root, save folder).</param>
    internal static ServerExposure ReadServerExposure(
        string? commandLine,
        string? processPath,
        params string?[] extraRoots) =>
        ReadServerExposure(commandLine, processPath, defaultDataPath: null, env: null, extraRoots);

    /// <summary>
    /// SERVERDESC1: the settings come from the game's own layering (<see cref="ServerHostSettingsFiles"/>, the same
    /// resolver Core uses): the install's default (under the first of <paramref name="extraRoots"/>, else the
    /// executable's folder), <c>&lt;persistentDataPath&gt;\Settings</c> (<c>-persistentDataPath</c>, else the
    /// user's LocalLow server folder, or <paramref name="defaultDataPath"/>), then the save folder's copy, each
    /// later file overwriting the keys it carries; then the environment variables and command-line arguments
    /// that override the listing flags, the address and the port (<c>VR_LISTONSTEAM</c>, <c>-listOnSteam</c>,
    /// <c>-enableListOnSteam</c>, <c>-address</c>, <c>-gamePort</c>, …), as the game applies them. Files the game
    /// does not read (<c>&lt;root&gt;\ServerHostSettings.json</c>, <c>&lt;install&gt;\Settings</c>) are no longer looked at.
    /// </summary>
    internal static ServerExposure ReadServerExposure(
        string? commandLine,
        string? processPath,
        string? defaultDataPath,
        Func<string, string?>? env,
        params string?[] extraRoots)
    {
        var exposure = new ServerExposure
        {
            CommandLine = commandLine,
            ProcessPath = processPath,
        };

        ServerHostSettingsResolution layers;
        try
        {
            var args = ServerHostSettingsFiles.SplitCommandLine(commandLine);
            if (ServerHostSettingsFiles.TryArg(args, "persistentDataPath", out var dataPath) && dataPath.Length > 0)
                exposure.SaveFolder = dataPath;

            string? install = null;
            if (extraRoots != null)
            {
                foreach (var root in extraRoots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    install = root;
                    break;
                }
            }

            install ??= TryParentDirectory(processPath);
            layers = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
            {
                Args = args,
                Env = env,
                InstallRoot = install,
                DefaultDataPath = defaultDataPath,
            });
        }
        catch (Exception e)
        {
            // Resolution never throws by contract; should it, the gate learns nothing and says so.
            exposure.SettingsPath = "(the settings could not be resolved: " + e.Message + ")";
            return exposure;
        }

        bool anyFile = false, sawPort = false, sawListing = false;
        string highest = "";
        var tried = new List<string>();
        foreach (var layer in layers.Layers)
        {
            if (layer.Path.Length > 0) tried.Add(layer.Path);
            if (!layer.Readable) continue;
            anyFile = true;
            highest = layer.Path;
            if (layer.TryGetBool("ListOnSteam", out var listSteam)) { exposure.ListOnSteam = listSteam; sawListing = true; }
            if (layer.TryGetBool("ListOnEOS", out var listEos)) { exposure.ListOnEOS = listEos; sawListing = true; }
            // "ListOnMasterServer" is not a stock key; honored so a wrapper that writes one is not ignored.
            if (layer.TryGetBool("ListOnMasterServer", out var listMaster))
            {
                sawListing = true;
                if (listMaster) exposure.ListOnSteam = true;
            }

            if (layer.TryGetBool("LanMode", out var lan)) exposure.LanMode = lan;
            if (layer.TryGetBool("PrivateGame", out var priv)) exposure.PrivateGame = priv;
            if (layer.TryGetString("Address", out var address) && !string.IsNullOrWhiteSpace(address)) exposure.Address = address;
            if (layer.TryGetInt("Port", out var port))
            {
                exposure.Port = port;
                sawPort = true;
            }
        }

        // The environment and the command line are applied after every file, the command line last.
        if (layers.OverrideOf("ListOnSteam")?.AsBool() is bool steamOverride) exposure.ListOnSteam = steamOverride;
        if (layers.OverrideOf("ListOnEOS")?.AsBool() is bool eosOverride) exposure.ListOnEOS = eosOverride;
        if (layers.OverrideOf("LanMode")?.AsBool() is bool lanOverride) exposure.LanMode = lanOverride;
        if (layers.OverrideOf("PrivateGame")?.AsBool() is bool privateOverride) exposure.PrivateGame = privateOverride;
        if (layers.OverrideOf("Address") is { } addressOverride && addressOverride.Value.Trim().Length > 0)
            exposure.Address = addressOverride.Value.Trim();
        if (layers.EffectiveInt("Port") is int effectivePort && layers.OverrideOf("Port") != null) exposure.Port = effectivePort;

        if (anyFile)
        {
            exposure.SettingsPath = highest;
            // R28 finding 10: the files must carry the keys the gate decides on, or the gate learned nothing.
            exposure.SettingsFound = sawPort && sawListing;
            exposure.SettingsIncomplete = !exposure.SettingsFound;
            return exposure;
        }

        exposure.SettingsFound = false;
        exposure.SettingsPath = tried.Count == 0
            ? Path.Combine("<persistentDataPath>", "Settings", ServerHostSettingsFileName)
            : string.Join("; ", tried);
        return exposure;
    }

    /// <summary>
    /// Parse the keys the gate cares about out of a <c>ServerHostSettings.json</c> body. Unknown
    /// keys are ignored and a malformed file returns false rather than throwing.
    ///
    /// <para>Returns true only when the file actually carried <see cref="RequiredSettingsKeys"/> —
    /// a <c>Port</c> and at least one recognized listing flag. R28 finding 10: the old version
    /// returned true for <c>{}</c>, so a stray JSON object called <c>ServerHostSettings.json</c> in
    /// the game root satisfied the gate on a server that was listed on Steam (the file the gate read
    /// was not the file the server used), and <c>"ListOnSteam": 1</c> or <c>"yes"</c> — which
    /// <see cref="TryBool"/> refuses — left the flag null and read as consent. An uninformative file
    /// is now "unknown", and unknown is refused unless the path is one of ours.</para>
    /// </summary>
    internal static bool ApplyServerHostSettings(string? json, ServerExposure into)
    {
        if (into == null || string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            bool sawListing = false;
            if (TryBool(root, "ListOnSteam", out var listSteam)) { into.ListOnSteam = listSteam; sawListing = true; }
            if (TryBool(root, "ListOnEOS", out var listEos)) { into.ListOnEOS = listEos; sawListing = true; }
            // "ListOnMasterServer" is not a stock key; accept it so a wrapper that writes one is
            // still honored rather than silently ignored.
            if (TryBool(root, "ListOnMasterServer", out var listMaster))
            {
                sawListing = true;
                if (listMaster) into.ListOnSteam = true;
            }

            if (TryBool(root, "LanMode", out var lan)) into.LanMode = lan;
            if (TryBool(root, "PrivateGame", out var priv)) into.PrivateGame = priv;
            if (TryString(root, "Address", out var address) && !string.IsNullOrWhiteSpace(address))
                into.Address = into.Address ?? address;

            bool sawPort = TryInt(root, "Port", out var port);
            if (sawPort && !into.Port.HasValue) into.Port = port;

            return sawPort && sawListing;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryParentDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try { return Path.GetDirectoryName(filePath); }
        catch { return null; }
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value)) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!TryProperty(root, name, out var element)) return false;
        if (element.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (element.ValueKind == JsonValueKind.False) { value = false; return true; }
        if (element.ValueKind == JsonValueKind.String)
            return bool.TryParse(element.GetString(), out value);
        return false;
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!TryProperty(root, name, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt32(out value);
        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        return false;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!TryProperty(root, name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return true;
    }

    /// <summary>
    /// Reads <c>-flag value</c> / <c>-flag=value</c> out of a command line. Quotes are stripped.
    /// </summary>
    internal static bool TryGetCommandArg(string? commandLine, string flag, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(flag)) return false;

        var parts = SplitCommandLine(commandLine!);
        for (int i = 0; i < parts.Count; i++)
        {
            var arg = parts[i];
            if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= parts.Count) return false;
                value = parts[i + 1];
                return value.Length > 0;
            }

            if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg.Substring(flag.Length + 1);
                return value.Length > 0;
            }
        }

        return false;
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>True when <paramref name="segment"/> is a whole directory name in the path.</summary>
    internal static bool ContainsPathSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment)) return false;
        try
        {
            var raw = path!.Replace('/', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in raw.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.Equals(part, segment, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* invalid path characters: fall through */ }

        return ContainsBoundedToken(path, segment);
    }

    /// <summary>
    /// Case-insensitive substring whose neighbors are not identifier characters, so
    /// <c>_testserverbackup</c> is not <c>_testserver</c>.
    /// </summary>
    internal static bool ContainsBoundedToken(string? text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return false;

        int start = 0;
        while (start < text!.Length)
        {
            int i = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool beforeOk = i == 0 || !IsTokenChar(text[i - 1]);
            int after = i + token.Length;
            bool afterOk = after >= text.Length || !IsTokenChar(text[after]);
            if (beforeOk && afterOk) return true;
            start = i + 1;
        }

        return false;
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ---------------------------------------------------------------------------------------
    // Token file ACL.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="restrict"/> over the token file and reports what the log line should
    /// say. Never throws: an unavailable or refused ACL API downgrades to
    /// <see cref="TokenAclDefault"/> with a warning, because a bridge that will not start is worse
    /// than a token file with the folder's default ACL — the file still holds a 192-bit secret and
    /// the socket is still loopback-only.
    /// </summary>
    internal static string ApplyTokenAcl(
        string? path,
        Action<string> restrict,
        Action<string>? warn = null,
        Func<string, string?>? verify = null)
    {
        if (string.IsNullOrWhiteSpace(path) || restrict == null)
        {
            warn?.Invoke("token file ACL not restricted: no token file on disk (session-only token)");
            return TokenAclDefault;
        }

        try
        {
            restrict(path!);
        }
        catch (Exception e)
        {
            warn?.Invoke(
                $"token file ACL not restricted ({e.GetType().Name}: {e.Message}); {path} keeps the " +
                "folder's default ACL — do not put it on a shared or synced drive");
            return TokenAclDefault;
        }

        // R28 finding 24: reporting "restricted" from the mere ABSENCE of an exception across nine
        // reflection steps is not evidence. A provider that silently no-ops the DACL, or a file
        // system that does not carry ACLs at all, logged `token acl=restricted` on a world-readable
        // file. Read it back; say `unverified` when the read-back cannot confirm it.
        if (verify == null) return TokenAclUnverified;

        string? problem;
        try { problem = verify(path!); }
        catch (Exception e) { problem = $"{e.GetType().Name}: {e.Message}"; }

        if (problem == null) return TokenAclRestricted;
        warn?.Invoke(
            $"token file ACL applied but NOT confirmed by read-back ({problem}); treat {path} as " +
            "readable by anything running as this user — do not put it on a shared or synced drive");
        return TokenAclUnverified;
    }

    /// <summary>
    /// Reads the token file's DACL back and returns null when it is exactly what
    /// <see cref="RestrictToCurrentUser"/> asked for — inheritance off, one access rule, and that
    /// rule for the current user. Otherwise returns a short description of what is wrong, which is
    /// what the log line reports.
    /// </summary>
    internal static string? VerifyRestrictedToCurrentUser(string path)
    {
        const string AclAssembly = "System.Security.AccessControl";
        const string FileAclAssembly = "System.IO.FileSystem.AccessControl";
        const string WindowsAssembly = "System.Security.Principal.Windows";

        var extensionsType = RequireType("System.IO.FileSystemAclExtensions", FileAclAssembly, AclAssembly);
        var identityType = RequireType("System.Security.Principal.WindowsIdentity", WindowsAssembly);
        var sidType = RequireType("System.Security.Principal.SecurityIdentifier", WindowsAssembly);

        var getAccessControl = extensionsType.GetMethod(
            "GetAccessControl",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(FileInfo) },
            null)
            ?? throw new MissingMethodException(extensionsType.FullName, "GetAccessControl");

        var security = getAccessControl.Invoke(null, new object[] { new FileInfo(path) })
            ?? throw new InvalidOperationException("GetAccessControl returned null");
        var securityType = security.GetType();

        var areProtected = securityType.GetMethod("AreAccessRulesProtected")
            ?? securityType.GetProperty("AreAccessRulesProtected")?.GetGetMethod();
        if (areProtected != null && areProtected.Invoke(security, null) is bool protectedFlag && !protectedFlag)
            return "the DACL still inherits from its folder";

        var getRules = securityType.GetMethod(
            "GetAccessRules", new[] { typeof(bool), typeof(bool), typeof(Type) })
            ?? throw new MissingMethodException(securityType.FullName, "GetAccessRules");
        var rules = getRules.Invoke(security, new object[] { true, false, sidType }) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("GetAccessRules returned nothing enumerable");

        var current = identityType.GetMethod("GetCurrent", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(identityType.FullName, "GetCurrent");
        var identity = current.Invoke(null, null)
            ?? throw new InvalidOperationException("WindowsIdentity.GetCurrent() returned null");
        var user = identityType.GetProperty("User")?.GetValue(identity)?.ToString();

        int count = 0;
        var others = new List<string>();
        foreach (var rule in rules)
        {
            count++;
            if (rule == null) { others.Add("(null)"); continue; }
            var who = rule.GetType().GetProperty("IdentityReference", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(rule)?.ToString();
            // R29 residual 21.5: this used to be guarded by `identityRefType != null`, which
            // RequireType can never produce — it throws instead — so the guard read as a defense
            // that was not one. The only thing that decides the rule is whether its identity is
            // this user's SID.
            if (who != null && string.Equals(who, user, StringComparison.OrdinalIgnoreCase))
                continue;
            others.Add(who ?? "(unknown)");
        }

        if (count == 0) return "the DACL has no access rules at all";
        if (others.Count > 0) return $"the DACL also grants {string.Join(", ", others)}";
        return null;
    }

    /// <summary>
    /// Replace the token file's DACL with one entry: this user, full control, inheritance off.
    ///
    /// Reached by reflection on purpose. <c>FileSecurity</c> and <c>FileSystemAclExtensions</c> are
    /// only in the reference pack for the <c>net6.0-windows</c> TFM, and the Dev plugin targets
    /// plain <c>net6.0</c> to match the rest of the tree; the assemblies themselves
    /// (<c>System.Security.AccessControl.dll</c>, <c>System.IO.FileSystem.AccessControl.dll</c>,
    /// <c>System.Security.Principal.Windows.dll</c>) do ship in the runtime BepInEx loads, so the
    /// call works at runtime. If a future runtime drops them, or the file system does not carry
    /// ACLs, this throws and <see cref="ApplyTokenAcl"/> logs a warning and carries on.
    /// </summary>
    internal static void RestrictToCurrentUser(string path)
    {
        const string AclAssembly = "System.Security.AccessControl";
        const string FileAclAssembly = "System.IO.FileSystem.AccessControl";
        const string WindowsAssembly = "System.Security.Principal.Windows";

        var fileSecurityType = RequireType("System.Security.AccessControl.FileSecurity", AclAssembly, FileAclAssembly);
        var rightsType = RequireType("System.Security.AccessControl.FileSystemRights", AclAssembly, FileAclAssembly);
        var accessTypeType = RequireType("System.Security.AccessControl.AccessControlType", AclAssembly, FileAclAssembly);
        var ruleType = RequireType("System.Security.AccessControl.FileSystemAccessRule", AclAssembly, FileAclAssembly);
        var extensionsType = RequireType("System.IO.FileSystemAclExtensions", FileAclAssembly, AclAssembly);
        var identityType = RequireType("System.Security.Principal.WindowsIdentity", WindowsAssembly);

        var current = identityType.GetMethod("GetCurrent", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(identityType.FullName, "GetCurrent");
        var identity = current.Invoke(null, null)
            ?? throw new InvalidOperationException("WindowsIdentity.GetCurrent() returned null");
        var user = identityType.GetProperty("User")?.GetValue(identity)
            ?? throw new InvalidOperationException("WindowsIdentity.GetCurrent().User is null");

        var fullControl = Enum.Parse(rightsType, "FullControl");
        var allow = Enum.Parse(accessTypeType, "Allow");
        var rule = Activator.CreateInstance(ruleType, new[] { user, fullControl, allow })
            ?? throw new InvalidOperationException("could not construct a FileSystemAccessRule");

        var security = Activator.CreateInstance(fileSecurityType)
            ?? throw new InvalidOperationException("could not construct a FileSecurity");

        var protect = fileSecurityType.GetMethod("SetAccessRuleProtection", new[] { typeof(bool), typeof(bool) })
            ?? throw new MissingMethodException(fileSecurityType.FullName, "SetAccessRuleProtection");
        // isProtected: stop inheriting the folder's ACEs. preserveInheritance: false, so nothing
        // the config folder grants (Users, a sync agent) is copied down onto the token.
        protect.Invoke(security, new object[] { true, false });

        var addRule = fileSecurityType.GetMethod("AddAccessRule", new[] { ruleType })
            ?? throw new MissingMethodException(fileSecurityType.FullName, "AddAccessRule");
        addRule.Invoke(security, new[] { rule });

        var setAccessControl = extensionsType.GetMethod(
            "SetAccessControl",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(FileInfo), fileSecurityType },
            null)
            ?? throw new MissingMethodException(extensionsType.FullName, "SetAccessControl");
        setAccessControl.Invoke(null, new object[] { new FileInfo(path), security });
    }

    private static Type RequireType(string fullName, params string[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var t = Type.GetType($"{fullName}, {assembly}", throwOnError: false);
            if (t != null) return t;
        }

        return Type.GetType(fullName, throwOnError: false)
            ?? throw new TypeLoadException($"{fullName} is not available in this runtime");
    }
}

/// <summary>
/// The path arithmetic behind the <c>C:\VRisingServer</c> fence — the one refusal in
/// <see cref="DevGate"/> that is described as "always" and is therefore the one that must not be
/// bypassable.
///
/// <para><b>R29 finding 6.</b> The fence used to be
/// <c>path.Replace('/','\\').TrimEnd('\\').StartsWith(root + "\\", OrdinalIgnoreCase)</c>. Case and
/// trailing separators were handled; nothing else was. Four ordinary forms walked through it and
/// would have installed the debug bridge on the user's live dedicated server:</para>
/// <list type="bullet">
/// <item><b>8.3 short names.</b> <c>C:\VRisingServer</c> is over eight characters, so on a default
/// NTFS volume it has an alias such as <c>C:\VRISIN~1</c>. A process launched through a shortcut, an
/// old launcher, <c>%~s1</c> in a batch file or some service managers reports
/// <c>ProcessPath = C:\VRISIN~1\VRisingServer.exe</c>.</item>
/// <item><b>Junctions and symlinks.</b> <c>D:\live</c> to <c>C:\VRisingServer</c>: every path under
/// <c>D:\live</c> passed.</item>
/// <item><b>Dot segments.</b> <c>C:\.\VRisingServer\VRisingServer.exe</c> is a legal path the string
/// compare does not collapse.</item>
/// <item><b>Extended-length and UNC forms.</b> <c>\\?\C:\VRisingServer\...</c> and
/// <c>\\localhost\C$\VRisingServer\...</c> both passed.</item>
/// </list>
///
/// <para><b>Now.</b> Both sides go through <see cref="Canonicalize"/>: strip <c>\\?\</c>,
/// <c>\\.\</c> and <c>\\?\UNC\</c>; rewrite a local admin share (<c>\\localhost\C$\x</c>,
/// <c>\\.\C:\x</c>, this machine's own name) to its drive form; <see cref="Path.GetFullPath"/> to
/// collapse <c>.</c> and <c>..</c> and to root a relative path (which on .NET/Windows also expands
/// an 8.3 alias of a directory that exists); then resolve the longest existing prefix on disk, which
/// expands the rest (<c>FileSystemInfo.FullName</c> reports the long name) and follows junctions and
/// symlinks to their final target
/// (<c>FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)</c>). The comparison is then
/// ordinal-ignore-case with a trailing separator.</para>
///
/// <para><b>What cannot be resolved is refused.</b> A path that still carries an 8.3-looking
/// segment (<c>NAME~1</c>) after that walk — because it does not exist on disk, or the volume
/// refused to expand it — counts as being under the root when it sits on the same volume as the
/// root. That is R29's stated minimum ("refuse any path segment matching <c>~[0-9]</c> and say so,
/// rather than silently allowing"), narrowed to the root's own volume so an unrelated
/// <c>D:\PROGRA~1</c> is not caught. The fence's failure mode is a refusal, which is the safe
/// side.</para>
///
/// <para>Windows-shaped by construction; every disk call is best-effort and non-throwing, so a
/// runtime without them degrades to the string canonicalization rather than to "allowed".</para>
/// </summary>
internal static class LivePathFence
{
    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it.</summary>
    internal static bool IsUnder(string? path, string root) => IsUnder(path, root, null);

    /// <summary>
    /// <see cref="IsUnder(string?,string)"/> with the on-disk resolution step replaced, so the tests
    /// can drive junction, short-name and missing-path cases without creating them.
    /// <paramref name="resolve"/> returns the resolved absolute path, or null to leave it alone.
    /// </summary>
    internal static bool IsUnder(string? path, string root, Func<string, string?>? resolve)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var canonicalRoot = Canonicalize(root, resolve);
        if (canonicalRoot.Length == 0) return false;

        var canonical = Canonicalize(path, resolve);
        if (canonical.Length == 0) return false;

        if (canonical.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)) return true;
        if (canonical.StartsWith(canonicalRoot + "\\", StringComparison.OrdinalIgnoreCase)) return true;

        // Unresolvable 8.3 alias on the root's own volume: refuse rather than guess (R29 finding 6).
        return HasUnresolvedShortName(canonical) && SameVolume(canonical, canonicalRoot);
    }

    /// <summary>
    /// The comparable form of <paramref name="path"/>: separators normalized, extended-length and
    /// local-share prefixes removed, <c>.</c> and <c>..</c> collapsed, the longest existing prefix
    /// resolved on disk, no trailing separator. Empty when there is nothing to compare.
    /// </summary>
    internal static string Canonicalize(string? path, Func<string, string?>? resolve = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var text = path!.Trim().Trim('"').Replace('/', '\\');
        text = StripPrefix(text);
        text = MapLocalShare(text);
        if (text.Length == 0) return "";

        try { text = Path.GetFullPath(text); }
        catch { /* an unrootable path is still compared in its stripped form */ }

        text = StripPrefix(text.Replace('/', '\\'));

        var resolved = (resolve ?? ResolveOnDisk)(text);
        if (!string.IsNullOrWhiteSpace(resolved))
            text = StripPrefix(resolved!.Trim().Replace('/', '\\'));

        return TrimTrailing(text);
    }

    /// <summary>
    /// Resolves the longest existing prefix of <paramref name="path"/> on disk and re-attaches the
    /// rest. <c>FileSystemInfo.FullName</c> reports the long form of an 8.3 alias, and
    /// <c>ResolveLinkTarget(returnFinalTarget: true)</c> follows a junction or symlink chain to its
    /// final target. Best-effort: any failure returns null and leaves the string form standing.
    /// </summary>
    private static string? ResolveOnDisk(string path)
    {
        try
        {
            var tail = new List<string>();
            var current = TrimTrailing(path);
            for (int guard = 0; guard < 64 && current.Length > 0; guard++)
            {
                bool isDirectory = Directory.Exists(current);
                if (isDirectory || File.Exists(current))
                {
                    FileSystemInfo info = isDirectory ? new DirectoryInfo(current) : new FileInfo(current);
                    FileSystemInfo? target;
                    try { target = info.ResolveLinkTarget(returnFinalTarget: true); }
                    catch { target = null; }

                    var head = TrimTrailing(StripPrefix((target ?? info).FullName.Replace('/', '\\')));
                    for (int i = tail.Count - 1; i >= 0; i--) head = head.TrimEnd('\\') + "\\" + tail[i];
                    return head;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent!.Equals(current, StringComparison.OrdinalIgnoreCase))
                    return null;
                tail.Add(current.Substring(parent.Length).Trim('\\'));
                current = TrimTrailing(parent);
            }
        }
        catch { /* best effort */ }

        return null;
    }

    /// <summary>Removes <c>\\?\</c>, <c>\\.\</c> and <c>\\?\UNC\</c>, leaving a plain path or a plain UNC.</summary>
    private static string StripPrefix(string text)
    {
        for (int guard = 0; guard < 4; guard++)
        {
            if (text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
                || text.StartsWith(@"\\.\UNC\", StringComparison.Ordinal))
            {
                text = @"\\" + text.Substring(8);
                continue;
            }

            if (text.StartsWith(@"\\?\", StringComparison.Ordinal)
                || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                text = text.Substring(4);
                continue;
            }

            break;
        }

        return text;
    }

    /// <summary>
    /// <c>\\localhost\C$\x</c>, <c>\\127.0.0.1\C$\x</c>, <c>\\.\C$\x</c> and
    /// <c>\\&lt;this machine&gt;\C$\x</c> all name a local drive; rewrite them to <c>C:\x</c> so the
    /// compare sees one form. A share on another host is left alone — it is not this machine's live
    /// server.
    /// </summary>
    private static string MapLocalShare(string text)
    {
        if (!text.StartsWith(@"\\", StringComparison.Ordinal)) return text;

        var rest = text.Substring(2);
        int slash = rest.IndexOf('\\');
        if (slash <= 0) return text;

        var host = rest.Substring(0, slash);
        if (!IsLocalHostName(host)) return text;

        var share = rest.Substring(slash + 1);
        int next = share.IndexOf('\\');
        var name = next < 0 ? share : share.Substring(0, next);
        var tail = next < 0 ? "" : share.Substring(next);

        // C$ (the admin share) and the bare C: form both appear in the wild.
        if (name.Length == 2 && char.IsLetter(name[0]) && (name[1] == '$' || name[1] == ':'))
            return name[0] + ":" + (tail.Length == 0 ? "\\" : tail);

        return text;
    }

    private static bool IsLocalHostName(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host == "." || host == "?") return true;
        if (host == "127.0.0.1" || host == "::1") return true;
        try { return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>True when a segment still looks like an 8.3 alias (<c>NAME~1</c>) after resolution.</summary>
    internal static bool HasUnresolvedShortName(string canonical)
    {
        foreach (var segment in canonical.Split('\\'))
        {
            int tilde = segment.IndexOf('~');
            if (tilde <= 0 || tilde + 1 >= segment.Length) continue;
            if (char.IsDigit(segment[tilde + 1])) return true;
        }

        return false;
    }

    /// <summary>True when both canonical paths sit on the same drive letter.</summary>
    private static bool SameVolume(string a, string b) =>
        a.Length >= 2 && b.Length >= 2 && a[1] == ':' && b[1] == ':'
        && char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0]);

    private static string TrimTrailing(string text)
    {
        var trimmed = text.TrimEnd('\\');
        // Keep the root's own separator: "C:" alone means the current directory on C:, not its root.
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed.Length == 0 ? text : trimmed;
    }
}
