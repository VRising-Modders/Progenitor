// PROGENITOR_EXTRA_INSTANCE gates the extra-instance Steam skip out of every build that is not a
// local dev build. src/Progenitor.Dev/Progenitor.Dev.csproj defines it for the Debug configuration
// only (that is what tools/deploy.ps1 builds); the Release build tools/package.ps1 makes never sets
// it, and tools/package.ps1 asserts the packaged tree carries neither this code nor the Dev DLL.
// Only ContainsBoundedToken stays compiled unconditionally: Ops/WorldSaveOps.cs uses it for the
// live-dedicated-server fence, which must exist in every build.
using System;
#if PROGENITOR_EXTRA_INSTANCE
using System.Collections.Generic;
using System.IO;
#if !PROGENITOR_TESTS
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using ProjectM;
using ProjectM.Shared;
using Progenitor;
using Progenitor.Persistence;
using Stunlock.Network;
#endif
using Progenitor.Hosted;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// Dev-only server skip of <c>ServerBootstrapSystem.Authenticate</c>'s Steam
/// <c>BeginAuthSession</c> for extra-instance fake Steam ids. Off by default
/// (<c>AllowExtraInstances</c>). Never runs on the live dedicated server at
/// <c>C:\VRisingServer</c>. The extra client still has to hail a Steam (not EOS-only)
/// platform id in <see cref="IsInBand"/> and a non-null ticket; this prefix only
/// replaces the Steam call that a dummy ticket cannot pass.
/// </summary>
/// <remarks>
/// Native from GameAssembly.dll (native analysis), server
/// <c>ProjectM.ServerBootstrapSystem$$Authenticate.c</c>: once
/// <c>hail.SessionTicket</c> is non-null, <c>BeginAuthSession</c> is unconditional;
/// result 0 calls <c>TryAuthenticate(connection, hail.Version, platformUserId, eosId,
/// usersTaken, maxConnectedUsers, connectedAsAdmin, hail.IsBot, hail.ClientSessionGuid,
/// defaultContentFlags: 0)</c>. <c>hail.IsBot</c>, <c>ServerHostSettings.Authenticate</c>,
/// and <c>LanMode</c> do not skip Steam. Interop signature (the IL viewer this pass):
/// <c>Authenticate(NetConnectionId, HailMessageStruct_Client, ulong, string, int, int, bool)</c>
/// — native name has no <c>byref_</c>; both wrapper types are passed by value, so Harmony
/// can prefix it. The prefix itself must not declare <c>HailMessageStruct_Client</c>
/// (Harmony matches by name; marshaling that ValueType wrapper on the trampoline is the
/// evidence §23 class of crash — R12 finding 12). <c>isBot</c> is therefore always
/// <c>false</c>. <c>OnConnectionApproval(ref NetBufferIn, …)</c> and
/// <c>ApproveClient(…, ref User, …)</c> stay unpatched (by-ref trap).
/// </remarks>
internal static class ExtraInstanceServerAuth
{
#if PROGENITOR_EXTRA_INSTANCE
    /// <summary>
    /// Extra-client SteamID64 band. Must stay equal to
    /// <c>ExtraInstanceAuth.PlatformIdBase .. + MaxInstanceIndex</c> (tests compile this
    /// file without that type). Inclusive.
    /// </summary>
    internal const ulong BandBase = 90000000000020000UL;

    /// <summary>Largest trailing extra-instance index accepted (inclusive).</summary>
    internal const int BandMaxIndex = 99;

    private const string LogPrefix = "extra-auth(server):";
    private const string NameToken = "Progenitor";
    private const string TestServerSegment = "_testserver";
    private const string HostedSaveToken = "progenitor-hosted";
    private static readonly string[] HostedSaveArgFlags = { "-saveName", "-serverName", "-serverSaveName", "-saveFileName" };

#if !PROGENITOR_TESTS
    private static readonly object Gate = new();
    private static bool _bound;
#endif

    /// <summary>
    /// Harmony prefix parameter names on <c>ServerBootstrapSystem.Authenticate</c>.
    /// Harmony matches by name; <c>hailData</c> / <c>HailMessageStruct_Client</c> / <c>__args</c>
    /// are omitted so the trampoline never marshals that ValueType wrapper (R12 finding 12).
    /// Must stay equal to <c>AuthenticateHook.Prefix</c> (compiled only outside tests).
    /// </summary>
    internal static readonly string[] AuthenticatePrefixParameterNames =
    {
        "__instance",
        "netConnectionId",
        "platformUserId",
        "eosId",
        "usersTaken",
        "maxConnectedUsers",
        "connectedAsAdmin",
    };

    /// <summary>
    /// Extra-instance skip cannot read hail <c>IsBot</c> without declaring
    /// <c>HailMessageStruct_Client</c> on the prefix. Extra clients hail <c>IsBot=false</c>
    /// anyway; pass this into <c>TryAuthenticate</c>.
    /// </summary>
    internal static bool ExtraInstanceIsBotWhenHailOmitted => false;

    /// <summary>
    /// True when a Harmony prefix parameter list would marshal the hail wrapper (by-value
    /// <c>HailMessageStruct_Client hailData</c>) or box every original argument (<c>__args</c>).
    /// </summary>
    internal static bool PrefixDeclaresHailWrapper(IReadOnlyList<string>? names)
    {
        if (names == null) return false;
        for (int i = 0; i < names.Count; i++)
        {
            var n = names[i];
            if (string.IsNullOrEmpty(n)) continue;
            if (n.Equals("hailData", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("__args", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.IndexOf("HailMessage", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    /// <summary>
    /// True for the extra-client fake-id band
    /// <c>[90000000000020000, 90000000000020099]</c>. Ghost leftover ids sit in
    /// <c>[90000000000000001, 90000000000010001)</c> and must not match.
    /// </summary>
    internal static bool IsInBand(ulong platformId)
    {
        ulong bandBase = BandBase;
        int maxIndex = BandMaxIndex;
#if !PROGENITOR_TESTS
        bandBase = ExtraInstanceAuth.PlatformIdBase;
        maxIndex = ExtraInstanceAuth.MaxInstanceIndex;
#endif
        return platformId >= bandBase && platformId <= bandBase + (ulong)maxIndex;
    }

    /// <summary>
    /// Whether this server is allowed to skip Steam for extra-instance fake ids.
    /// The live dedicated root <see cref="HostedPathRules.ForbiddenLiveServerRoot"/> is
    /// always refused. Otherwise the process must be the staged test server
    /// (<c>_testserver</c> path segment) or a hosted child whose save name is
    /// <c>progenitor-hosted</c> (argv <c>-saveName</c> / <c>-serverName</c> /
    /// <c>-serverSaveName</c>, or that token as a path segment). A vanilla
    /// <c>CloudSaves</c> path is not enough: a production private game also uses
    /// that folder (R11 finding 9).
    /// </summary>
    /// <param name="serverName"><c>ServerHostSettings.Name</c>; may be null before settings load.</param>
    /// <param name="saveFolder">World save folder, BepInEx root, or any other path that locates this process.</param>
    /// <param name="processPath"><c>VRisingServer.exe</c> full path when known.</param>
    /// <param name="commandLine">Process command line (hosted argv includes <c>progenitor-hosted</c>).</param>
    /// <param name="reason">Why it was allowed or refused. Never null.</param>
    internal static bool TryAllowEnable(
        string? serverName,
        string? saveFolder,
        string? processPath,
        string? commandLine,
        out string reason)
    {
        if (HostedPathRules.IsForbiddenLiveServerPath(saveFolder)
            || HostedPathRules.IsForbiddenLiveServerPath(processPath)
            || ContainsBoundedToken(commandLine, HostedPathRules.ForbiddenLiveServerRoot)
            || ContainsBoundedToken(saveFolder, HostedPathRules.ForbiddenLiveServerRoot)
            || ContainsBoundedToken(processPath, HostedPathRules.ForbiddenLiveServerRoot))
        {
            reason =
                $"refusing the live dedicated server path ({HostedPathRules.ForbiddenLiveServerRoot}); extra-instance Steam skip is never installed there";
            return false;
        }

        if (HasTestServerSignal(saveFolder, processPath, commandLine))
        {
            reason = "path identifies the staged Progenitor test server (_testserver)";
            return true;
        }

        if (HasHostedSignal(saveFolder, processPath, commandLine))
        {
            reason = "path/argv identifies a Progenitor hosted child (save name progenitor-hosted)";
            return true;
        }

        if (NameContainsProgenitor(serverName))
        {
            reason =
                "ServerHostSettings.Name contains 'Progenitor' but save/process is not under _testserver and the save name is not progenitor-hosted; name alone is not enough";
            return false;
        }

        reason =
            "save/process is not under _testserver and the save name is not progenitor-hosted";
        return false;
    }

    /// <summary>
    /// True when <paramref name="serverName"/> contains <c>Progenitor</c>
    /// (ordinal, case-insensitive). The staged test server is named
    /// <c>Progenitor Test</c>; the hosted child is named <c>progenitor-hosted</c>.
    /// </summary>
    internal static bool NameContainsProgenitor(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return false;
        return serverName.IndexOf(NameToken, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// True when <paramref name="path"/> has <paramref name="segment"/> as a whole
    /// directory name. <c>_testserverbackup</c> is not <c>_testserver</c>.
    /// </summary>
    internal static bool ContainsPathSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment))
            return false;

        try
        {
            var raw = path.Replace('/', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parts = raw.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in parts)
            {
                if (string.Equals(part, segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            /* invalid path characters: fall through to bounded-token */
        }

        return ContainsBoundedToken(path, segment);
    }
#endif

    /// <summary>
    /// Case-insensitive substring whose neighbors are not identifier characters, so
    /// <c>_testserverbackup</c> does not count as <c>_testserver</c>. Compiled in every build:
    /// <see cref="WorldSaveOps"/> uses it for the live-dedicated-server fence.
    /// </summary>
    internal static bool ContainsBoundedToken(string? text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return false;

        int start = 0;
        while (start < text.Length)
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

#if PROGENITOR_EXTRA_INSTANCE
#if !PROGENITOR_TESTS
    /// <summary>
    /// Harmony prefix on <see cref="ServerBootstrapSystem.Authenticate"/> when the config
    /// switch is on and <see cref="TryAllowEnable"/> accepts this process. No-op on the
    /// client, when the switch is off, or when already installed. Safe to call twice.
    /// </summary>
    internal static void Install(bool allowExtraInstances, int loopbackPort = 0)
    {
        lock (Gate)
        {
            if (!Progenitor.Plugin.IsServerProcess)
            {
                Line("skip install: not a server process");
                return;
            }

            if (_bound)
            {
                Line("already installed");
                return;
            }

            if (!allowExtraInstances)
            {
                Line("AllowExtraInstances=false; extra-instance Steam skip is off");
                return;
            }

            string? name = TryServerName();
            string? save = TrySaveFolder();
            string? process = TryProcessPath();
            string? cmd = TryCommandLine();
            string? bep = TryBepInExRoot();
            string? pathForRule = FirstNonEmpty(save, bep, process);

            if (!TryAllowEnable(name, pathForRule, process, cmd, out var enableReason))
            {
                try
                {
                    Plugin.Logger?.LogError(
                        $"{LogPrefix} AllowExtraInstances=true but this is not a Progenitor test/hosted server ({enableReason}). Steam skip NOT installed.");
                }
                catch { /* logger can be gone */ }
                return;
            }

            if (!IsInBand(ExtraInstanceAuth.PlatformIdBase)
                || !ExtraInstanceAuth.IsOwnPlatformId(ExtraInstanceAuth.PlatformIdBase)
                || IsInBand(ExtraInstanceAuth.PlatformIdBase - 1UL)
                || ExtraInstanceAuth.IsOwnPlatformId(ExtraInstanceAuth.PlatformIdBase - 1UL)
                || !IsInBand(ExtraInstanceAuth.PlatformIdBase + (ulong)ExtraInstanceAuth.MaxInstanceIndex)
                || IsInBand(ExtraInstanceAuth.PlatformIdBase + (ulong)ExtraInstanceAuth.MaxInstanceIndex + 1UL))
            {
                Line("band mismatch with ExtraInstanceAuth; Steam skip NOT installed");
                return;
            }

            if (!TryInspectAuthenticate(out var inspectDetail))
            {
                Line($"Authenticate is not Harmony-safe ({inspectDetail}); Steam skip NOT installed. See extra-client-auth.md.");
                return;
            }

            try
            {
                if (!HarmonySlotHandoff.TryBindPlugin(
                        HarmonySlotHandoff.Authenticate,
                        new AuthenticateHandler(AuthenticateHook.Prefix)))
                {
                    Line($"Authenticate slot bind failed ({inspectDetail}); Steam skip NOT installed.");
                    return;
                }

                _bound = true;
                // Dedicated servers keep their Steam transport; extra instances join over a
                // loopback-only Lidgren listener added beside it (LoopbackTransport).
                try { LoopbackTransport.Start(loopbackPort); }
                catch (Exception e) { Line($"loopback transport start: {e.Message}"); }
                try
                {
                    Plugin.Logger?.LogWarning(
                        $"{LogPrefix} AllowExtraInstances=true — extra-instance Steam skip is ON (dev only). " +
                        $"Fake Steam ids {ExtraInstanceAuth.PlatformIdBase}..{ExtraInstanceAuth.PlatformIdBase + (ulong)ExtraInstanceAuth.MaxInstanceIndex} " +
                        $"skip BeginAuthSession and call TryAuthenticate (isBot=false; hail wrapper omitted from the prefix). " +
                        $"enable={enableReason}. Inspected {inspectDetail}. Never enable this on a public server.");
                }
                catch { /* logger can be gone */ }
            }
            catch (Exception e)
            {
                try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.Authenticate); } catch { /* best-effort */ }
                _bound = false;
                Line($"install threw: {e}");
            }
        }
    }

    /// <summary>Unpatch. Called from the Dev plugin Unload so a reload does not stack prefixes.</summary>
    internal static void Shutdown()
    {
        lock (Gate)
        {
            try { LoopbackTransport.Stop(); } catch { /* best-effort */ }
            if (!_bound) return;
            try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.Authenticate); }
            catch (Exception e) { Line($"slot clear: {e.Message}"); }
            _bound = false;
            Line("uninstalled");
        }
    }

    /// <summary>
    /// Confirms <see cref="ServerBootstrapSystem.Authenticate"/> has no by-ref struct /
    /// by-ref <c>Il2CppSystem.ValueType</c> parameter. If <c>NetConnectionId</c> or
    /// <c>HailMessageStruct_Client</c> is passed by ref, Harmony must not prefix it
    /// (same trampoline that killed the extra process on
    /// <c>PlatformSystemBase.GetAuthSessionTicket</c>, evidence §23).
    /// </summary>
    private delegate bool AuthenticateHandler(
        ServerBootstrapSystem instance,
        NetConnectionId netConnectionId,
        ulong platformUserId,
        string eosId,
        int usersTaken,
        int maxConnectedUsers,
        bool connectedAsAdmin);

    private static bool TryInspectAuthenticate(out string detail)
    {
        detail = "";
        try
        {
            var original = typeof(ServerBootstrapSystem).GetMethod(
                nameof(ServerBootstrapSystem.Authenticate),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (original == null)
            {
                detail = "ServerBootstrapSystem.Authenticate not found on the interop wrapper";
                return false;
            }

            var pars = original.GetParameters();
            var desc = new System.Text.StringBuilder();
            desc.Append(original.DeclaringType?.FullName).Append('.').Append(original.Name).Append('(');
            for (int i = 0; i < pars.Length; i++)
            {
                if (i > 0) desc.Append(", ");
                var p = pars[i];
                if (p.IsOut) desc.Append("out ");
                else if (p.ParameterType.IsByRef) desc.Append("ref ");
                var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                desc.Append(t?.FullName ?? p.ParameterType.Name);
                desc.Append(' ').Append(p.Name);

                if (p.ParameterType.IsByRef)
                {
                    var elem = p.ParameterType.GetElementType();
                    if (elem != null && IsByRefStructTrap(elem))
                    {
                        detail =
                            $"{desc}) — by-ref {elem.FullName} {p.Name}; Harmony must not prefix this (by-ref struct trap)";
                        return false;
                    }
                }
            }

            desc.Append(") returns ").Append(original.ReturnType.FullName);
            desc.Append(" native=").Append(original.Name);
            detail = desc.ToString();
            return true;
        }
        catch (Exception e)
        {
            detail = "inspect threw: " + e;
            return false;
        }
    }

    private static bool IsByRefStructTrap(Type t)
    {
        if (t.IsValueType) return true;
        try
        {
            return typeof(Il2CppSystem.ValueType).IsAssignableFrom(t);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryServerName()
    {
        try
        {
            var host = SettingsManager.ServerHostSettings;
            return host == null ? null : host.Name;
        }
        catch (Exception e)
        {
            Line($"ServerHostSettings.Name threw: {e.Message}");
            return null;
        }
    }

    private static string? TrySaveFolder()
    {
        try { return ModSave.CurrentSaveDirectory; }
        catch (Exception e)
        {
            Line($"ModSave.CurrentSaveDirectory threw: {e.Message}");
            return null;
        }
    }

    private static string? TryProcessPath()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }
        catch (Exception e)
        {
            Line($"process path threw: {e.Message}");
            return null;
        }
    }

    private static string? TryCommandLine()
    {
        try { return Environment.CommandLine; }
        catch { return null; }
    }

    private static string? TryBepInExRoot()
    {
        try { return Paths.BepInExRootPath; }
        catch { return null; }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }

        return null;
    }

    private static void Line(string message)
    {
        try { Plugin.Logger?.LogInfo($"{LogPrefix} {message}"); }
        catch { /* logger can be gone during Unload */ }
    }

    /// <summary>
    /// Skip Steam when <paramref name="platformUserId"/> is in the extra-instance band and
    /// call the same <c>TryAuthenticate</c> overload the OK branch uses. Harmony matches
    /// prefix parameters by name: <c>HailMessageStruct_Client hailData</c> is omitted so the
    /// trampoline does not marshal that ValueType wrapper (R12 finding 12 / evidence §23).
    /// <c>isBot</c> is therefore <see cref="ExtraInstanceIsBotWhenHailOmitted"/> (false).
    /// Version is the server's own network version (OnConnectionApproval already matched
    /// the hail). Session guid is default. Every other platform id runs the original.
    /// </summary>
    private static class AuthenticateHook
    {
        public static bool Prefix(
            ServerBootstrapSystem __instance,
            NetConnectionId netConnectionId,
            ulong platformUserId,
            string eosId,
            int usersTaken,
            int maxConnectedUsers,
            bool connectedAsAdmin)
        {
            try
            {
                if (!IsInBand(platformUserId))
                    return true;

                if (__instance == null)
                {
                    Line($"band id {platformUserId} but ServerBootstrapSystem is null; running original");
                    return true;
                }

                // Extra instances run on the server machine itself. Anything else claiming a band
                // id goes to the original path, where Steam rejects the dummy ticket.
                if (!TryLoopbackEndpoint(netConnectionId, out string endpoint))
                {
                    try
                    {
                        Plugin.Logger?.LogWarning(
                            $"{LogPrefix} band id {platformUserId} from {endpoint} is not a loopback Lidgren connection; Steam decides");
                    }
                    catch { /* logger can be gone */ }
                    return true;
                }

                bool isBot = ExtraInstanceIsBotWhenHailOmitted;
                int version = TryNetworkVersion();
                var sessionGuid = default(Il2CppSystem.Guid);
                __instance.TryAuthenticate(
                    netConnectionId,
                    version,
                    platformUserId,
                    eosId,
                    usersTaken,
                    maxConnectedUsers,
                    connectedAsAdmin,
                    isBot,
                    sessionGuid,
                    UserContentFlags.None);
                try
                {
                    Plugin.Logger?.LogWarning(
                        $"{LogPrefix} approved {platformUserId} without Steam (dev only) isBot={isBot} version={version} (hail wrapper omitted)");
                }
                catch { /* logger can be gone */ }
                return false;
            }
            catch (Exception e)
            {
                Line($"Authenticate prefix threw: {e}");
                return true;
            }
        }
    }

    /// <summary>
    /// True only for a Lidgren connection whose remote address is loopback. A Steam or EOS
    /// connection id, or any failure to read the endpoint, is not loopback.
    /// </summary>
    private static bool TryLoopbackEndpoint(NetConnectionId connection, out string endpoint)
    {
        endpoint = "unknown";
        try
        {
            if (connection == null) return false;
            var lidgren = connection.TryCast<Stunlock.Network.Lidgren.LidgrenNetConnectionId>();
            if (lidgren == null)
            {
                endpoint = connection.ToString() ?? "non-Lidgren";
                return false;
            }
            var address = lidgren.IPEndPoint?.Address?.ToString();
            endpoint = address ?? "no address";
            return IsLoopbackAddressText(address);
        }
        catch (Exception e)
        {
            endpoint = "unreadable (" + e.GetType().Name + ")";
            return false;
        }
    }
#endif

    /// <summary>Pure check, unit-tested: an IPv4/IPv6 loopback address in text form.</summary>
    internal static bool IsLoopbackAddressText(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        return System.Net.IPAddress.TryParse(address.Trim(), out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// <c>GameVersionUtils.GetNetworkVersion</c> — the same int OnConnectionApproval already
    /// compared to the hail before calling Authenticate. 0 when the getter throws.
    /// </summary>
    private static int TryNetworkVersion()
    {
        try { return GameVersionUtils.GetNetworkVersion(); }
        catch (Exception e)
        {
            Line($"GetNetworkVersion threw: {e.Message}; TryAuthenticate version=0");
            return 0;
        }
    }
#endif

    private static bool HasTestServerSignal(string? saveFolder, string? processPath, string? commandLine)
    {
        return ContainsPathSegment(saveFolder, TestServerSegment)
            || ContainsPathSegment(processPath, TestServerSegment)
            || ContainsBoundedToken(commandLine, TestServerSegment);
    }

    private static bool HasHostedSignal(string? saveFolder, string? processPath, string? commandLine)
    {
        if (ContainsBoundedToken(saveFolder, HostedSaveToken)
            || ContainsBoundedToken(processPath, HostedSaveToken)
            || ContainsBoundedToken(commandLine, HostedSaveToken))
            return true;

        foreach (var flag in HostedSaveArgFlags)
        {
            if (TryGetCommandArg(commandLine, flag, out var value) && IsHostedSaveName(value))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="name"/> is exactly <c>progenitor-hosted</c>
    /// (ordinal, case-insensitive). A vanilla private-game save name is not.
    /// </summary>
    internal static bool IsHostedSaveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Trim().Equals(HostedSaveToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads <c>-saveName value</c> / <c>-saveName=value</c> (and the other hosted save
    /// flags) from a command line. Quotes around the value are stripped.
    /// </summary>
    internal static bool TryGetCommandArg(string? commandLine, string flag, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(flag))
            return false;

        var parts = SplitCommandLine(commandLine);
        for (int i = 0; i < parts.Count; i++)
        {
            var arg = parts[i];
            if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= parts.Count) return false;
                value = Unquote(parts[i + 1]);
                return value.Length > 0;
            }

            if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = Unquote(arg.Substring(flag.Length + 1));
                return value.Length > 0;
            }
        }

        return false;
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static string Unquote(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
            return t.Substring(1, t.Length - 2);
        return t;
    }
#endif

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
