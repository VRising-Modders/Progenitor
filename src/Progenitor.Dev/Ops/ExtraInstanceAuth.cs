using System;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using ProjectM.Auth;
using Progenitor;
using Stunlock.Network;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Extra-instance-only Steam skip. A second <c>VRising.exe</c> on one machine has no Steam
/// client (evidence §19: <c>PlatformSystemBase.GetAuthSessionTicket</c> throws
/// <c>Steamworks.InteropHelp.TestIfAvailableClient</c> before any hail). When
/// <c>PROGENITOR_INSTANCE</c> is set, a Bootstrap-owned prefix on
/// <see cref="ClientWorldManager.GetAuthSessionTicket"/> skips that call and continues the
/// LAN connect with platform id <c>90000000000020000 + index</c> and a dummy 64-byte ticket
/// via <see cref="ClientBootstrapSystem.Connect"/>. A postfix on
/// <see cref="ConnectAddress.CreateSteamIPv4"/> rewrites the address to
/// <see cref="ConnectAddress.CreateLidgrenIPv4"/> because SteamIPv4 needs Steam Networking
/// Sockets, which that extra process also failed to init. Hail <c>IsBot</c> is forced on
/// stored <see cref="ClientConnectData"/> (see remarks) so the server can skip
/// <c>BeginAuthSession</c> if it branches on that flag.
/// </summary>
/// <remarks>
/// Installed from the Dev plugin only on the client, and only when
/// <c>PROGENITOR_INSTANCE</c> is non-empty. The primary Steam client leaves that env unset
/// and is not patched. Server files are never touched; the staged test server still needs
/// <c>LanMode = true</c> (Lidgren listen). <c>Authenticate = false</c> is stored but is
/// not what skips Steam on this build.
/// The hail must carry a Steam (or PSN) platform id: an EOS-only id is disconnected
/// from <c>OnConnectionApproval</c> with reason <c>0x11</c> before <c>Authenticate</c>.
/// A dummy non-null ticket is required (null/empty deserializes as a null pointer and
/// is denied inside <c>Authenticate</c>). Passing Steam is a <em>server</em> Dev prefix
/// on <c>ServerBootstrapSystem.Authenticate</c> (<see cref="ExtraInstanceServerAuth"/>),
/// off by default.
/// Do not Harmony-patch <see cref="PlatformSystemBase.GetAuthSessionTicket"/>: it takes
/// <c>ref SteamNetworkingIdentity</c> (an <c>Il2CppSystem.ValueType</c> wrapper) and the
/// prefix trampoline silently killed the extra process (evidence §23) before any managed
/// log line from that prefix.
/// Do not Harmony-patch <see cref="HailMessageStruct_Client.FromConnectData"/>: native
/// name <c>FromConnectData_Public_Static_Void_byref_ClientConnectData_byref_HailMessageStruct_Client_0</c>
/// — both arguments are by-ref ValueType wrappers (the same trap).
/// </remarks>
internal static class ExtraInstanceAuth
{
    /// <summary>
    /// First extra-client SteamID64. Index 1 (env <c>client-1</c>) is
    /// <c>90000000000020001</c>. Lives outside the ghost leftover window
    /// <c>[GhostOps.PlatformIdBase, GhostOps.PlatformIdBase + GhostOps.PlatformIdSpan)</c>
    /// so a <c>ghost destroy</c> / Dev Unload leftover sweep cannot take a live extra-client
    /// User. Occupied range is <c>[PlatformIdBase, PlatformIdBase + MaxInstanceIndex]</c>
    /// inclusive (index 0..99).
    /// </summary>
    internal const ulong PlatformIdBase = 90000000000020000UL;

    /// <summary>Largest trailing index accepted by <see cref="ParseInstanceIndex"/> (inclusive).</summary>
    internal const int MaxInstanceIndex = 99;

    private const string LogPrefix = "extra-auth:";

    private static readonly object Gate = new();
    private static bool _bound;
    private static string _instanceTag = "";
    private static int _instanceIndex;
    private static ulong _platformId;
    private static bool _editorSettingsApplied;
    private static bool _updateConnectWroteThisAttempt;

    /// <summary>
    /// True for the extra-client fake-id band. <see cref="GhostOps.IsGhostPlatformId"/>
    /// excludes this so leftover-ghost destroy never removes a live extra-client User.
    /// </summary>
    internal static bool IsOwnPlatformId(ulong platformId) =>
        platformId >= PlatformIdBase && platformId <= PlatformIdBase + (ulong)MaxInstanceIndex;

    /// <summary>
    /// Slot binds for this extra process. No-op on the server, on the primary client
    /// (env unset), or when already installed. Safe to call twice.
    /// </summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (Progenitor.Plugin.IsServerProcess)
            {
                Line("skip install: server process");
                return;
            }

            var tag = Environment.GetEnvironmentVariable("PROGENITOR_INSTANCE")?.Trim() ?? "";
            if (tag.Length == 0)
            {
                Line("skip install: PROGENITOR_INSTANCE is unset (primary client keeps real Steam tickets)");
                return;
            }

            if (_bound)
            {
                Line($"already installed instance={_instanceTag} index={_instanceIndex} platformId={_platformId}");
                return;
            }

            _instanceTag = tag;
            _instanceIndex = ParseInstanceIndex(tag);
            _platformId = PlatformIdBase + (ulong)_instanceIndex;

            try
            {
                if (!TryBindGetAuthSessionTicket())
                {
                    ClearBinds();
                    _bound = false;
                    Line("install failed: ClientWorldManager.GetAuthSessionTicket seam not bound (not patching PlatformSystemBase.GetAuthSessionTicket; by-ref SteamNetworkingIdentity trampoline crashed the extra process, evidence §23)");
                    return;
                }

                TryBindCreateSteamIPv4();
                LogFromConnectDataSkip();
                bool connectIsBot = TryBindClientBootstrapConnect();
                bool updateIsBot = TryBindUpdateConnectRequest();
                // AllowSkipAuth is never poked (R5 finding 1). FakePlatformId /
                // IgnoreSteamInitializationFailure wait for the UseFakePlatformId fallback.
                _bound = true;

                if (!connectIsBot)
                    Line("install: ClientBootstrapSystem.Connect IsBot seam is NOT in");
                if (!updateIsBot)
                    Line("install: ClientBootstrapSystem.UpdateConnectRequest IsBot seam is NOT in");
                if (!connectIsBot && !updateIsBot)
                    Line("install: IsBot seams NOT in — dummy-ticket hail continues with ClientConnectData.IsBot left false (ticket prefix stayed; that is the proven hail)");

                string isBotSummary;
                if (connectIsBot && updateIsBot)
                    isBotSummary = "isBot=ClientConnectData._LastConnectData (FromConnectData not patched: by-ref)";
                else if (connectIsBot)
                    isBotSummary = "isBot=ClientBootstrapSystem.Connect (UpdateConnectRequest IsBot seam is NOT in; FromConnectData not patched: by-ref)";
                else if (updateIsBot)
                    isBotSummary = "isBot=UpdateConnectRequest (Connect IsBot seam is NOT in; FromConnectData not patched: by-ref)";
                else
                    isBotSummary = "IsBot seams NOT in (Connect and UpdateConnectRequest patches failed; FromConnectData not patched: by-ref)";

                Line(
                    $"installed instance={_instanceTag} index={_instanceIndex} platformId={_platformId} " +
                    "seam=ClientWorldManager.GetAuthSessionTicket->ClientBootstrapSystem.Connect " +
                    isBotSummary);
            }
            catch (Exception e)
            {
                ClearBinds();
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
            if (!_bound)
            {
                _instanceTag = "";
                _instanceIndex = 0;
                _platformId = 0;
                _editorSettingsApplied = false;
                _updateConnectWroteThisAttempt = false;
                return;
            }

            ClearBinds();
            _bound = false;
            Line($"uninstalled instance={_instanceTag}");
            _instanceTag = "";
            _instanceIndex = 0;
            _platformId = 0;
            _editorSettingsApplied = false;
            _updateConnectWroteThisAttempt = false;
        }
    }

    /// <summary>
    /// <c>client-1</c> → 1, <c>7</c> → 7. Anything else logs and falls back to 1 so the
    /// platform id is still in the extra-client band rather than a real SteamId.
    /// </summary>
    internal static int ParseInstanceIndex(string tag)
    {
        var raw = tag.Trim();
        int dash = raw.LastIndexOf('-');
        var numPart = dash >= 0 ? raw.Substring(dash + 1) : raw;
        if (int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n is >= 0 and <= MaxInstanceIndex)
        {
            return n;
        }

        Line($"PROGENITOR_INSTANCE='{tag}' has no trailing 0..99 index; using 1");
        return 1;
    }

    // ---- slots ------------------------------------------------------------------------------

    private delegate bool AuthTicketHandler(bool isLAN, World clientWorld, ConnectAddress connectAddress, ConnectAddress fallbackConnectAddress, string password);
    private delegate void SteamAddressHandler(string address, ushort port, ref ConnectAddress result);
    private delegate void ConnectPrefixHandler();
    private delegate void ConnectPostfixHandler(ClientBootstrapSystem instance);
    private delegate void UpdateConnectHandler(ClientBootstrapSystem instance);

    private static void ClearBinds()
    {
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.GetAuthSessionTicket); } catch { /* best-effort */ }
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.CreateSteamIPv4); } catch { /* best-effort */ }
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.ClientBootstrapConnectPrefix); } catch { /* best-effort */ }
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.ClientBootstrapConnectPostfix); } catch { /* best-effort */ }
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.UpdateConnectRequest); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Managed caller of <c>PlatformSystemBase.GetAuthSessionTicket</c>. Native name
    /// <c>GetAuthSessionTicket_Private_Void_SteamNetworkingIdentity_Boolean_World_ConnectAddress_ConnectAddress_String_0</c>
    /// — identity is by value (wrapper class), not <c>ref</c>. Bootstrap intercepts this;
    /// the by-ref sibling on <c>PlatformSystemBase</c> cannot (evidence §23).
    /// </summary>
    private static bool TryBindGetAuthSessionTicket()
    {
        try
        {
            if (!HarmonySlotHandoff.TryBindPlugin(
                    HarmonySlotHandoff.GetAuthSessionTicket,
                    new AuthTicketHandler(ManagerTicketHook.Prefix)))
            {
                Line("ClientWorldManager.GetAuthSessionTicket slot bind failed (Bootstrap trampoline missing)");
                return false;
            }

            Line("bound ClientWorldManager.GetAuthSessionTicket (skip Steam, Connect with fake id + dummy ticket)");
            return true;
        }
        catch (Exception e)
        {
            Line($"ClientWorldManager.GetAuthSessionTicket slot bind failed: {e}");
            return false;
        }
    }

    private static bool TryBindCreateSteamIPv4()
    {
        try
        {
            if (!HarmonySlotHandoff.TryBindPlugin(
                    HarmonySlotHandoff.CreateSteamIPv4,
                    new SteamAddressHandler(AddressHook.Postfix)))
            {
                Line("ConnectAddress.CreateSteamIPv4 slot bind failed; extra connect will keep SteamIPv4 and likely fail to hail");
                return false;
            }

            Line("bound ConnectAddress.CreateSteamIPv4 -> CreateLidgrenIPv4 (Steam networking needs Steam)");
            return true;
        }
        catch (Exception e)
        {
            Line($"ConnectAddress.CreateSteamIPv4 slot bind failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// <see cref="HailMessageStruct_Client.FromConnectData"/> is the copy that includes
    /// <c>IsBot</c>. Interop signature is
    /// <c>static void FromConnectData([In] ref ClientConnectData, out HailMessageStruct_Client)</c>
    /// — both types are <c>Il2CppSystem.ValueType</c> wrappers passed by-ref. Harmony must
    /// not patch it (evidence §23 trampoline). The hail flag is forced on stored
    /// <see cref="ClientConnectData"/> instead.
    /// </summary>
    private static void LogFromConnectDataSkip()
    {
        try
        {
            var original = typeof(HailMessageStruct_Client).GetMethod(
                nameof(HailMessageStruct_Client.FromConnectData),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            if (original == null)
            {
                Line("HailMessageStruct_Client.FromConnectData not found on the interop wrapper; not patching (would have been by-ref anyway)");
                return;
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
            }
            desc.Append(") returns ").Append(original.ReturnType.FullName);
            Line(
                $"not patching {desc} — native FromConnectData_Public_Static_Void_byref_ClientConnectData_byref_HailMessageStruct_Client_0 " +
                "(by-ref ValueType wrappers; Harmony trampoline on this shape crashed the extra process, evidence §23). " +
                "Forcing ClientConnectData.IsBot on ClientBootstrapSystem.Connect / UpdateConnectRequest instead.");
        }
        catch (Exception e)
        {
            Line($"FromConnectData inspect threw: {e}");
        }
    }

    /// <summary>
    /// <see cref="ClientBootstrapSystem.Connect"/> is the method that builds
    /// <see cref="ClientConnectData"/>. Signature has no by-ref struct: two
    /// <see cref="ConnectAddress"/> values (real C# struct, by value), a
    /// <see cref="PlatformIdCollection"/> wrapper, a byte array, and a string.
    /// Prefix resets the per-attempt UpdateConnectRequest write flag. Postfix writes
    /// <c>IsBot = true</c> onto <c>_LastConnectData</c> so a later <c>FromConnectData</c>
    /// copy (queued connect) carries the bot flag. If Connect serializes the hail before
    /// returning, this postfix is too late for the first hail — native detour of
    /// <c>FromConnectData</c> is deferred pending the analyze of server
    /// <c>Authenticate</c> (R5 finding 2).
    /// </summary>
    private static bool TryBindClientBootstrapConnect()
    {
        try
        {
            bool pre = HarmonySlotHandoff.TryBindPlugin(
                HarmonySlotHandoff.ClientBootstrapConnectPrefix,
                new ConnectPrefixHandler(ConnectIsBotHook.Prefix));
            bool post = HarmonySlotHandoff.TryBindPlugin(
                HarmonySlotHandoff.ClientBootstrapConnectPostfix,
                new ConnectPostfixHandler(ConnectIsBotHook.Postfix));
            if (!pre || !post)
            {
                Line("ClientBootstrapSystem.Connect slot bind failed; cannot force ClientConnectData.IsBot");
                return false;
            }

            Line("bound ClientBootstrapSystem.Connect prefix+postfix (force ClientConnectData.IsBot=true; FromConnectData not patched)");
            return true;
        }
        catch (Exception e)
        {
            Line($"ClientBootstrapSystem.Connect slot bind failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// No-arg instance method. If <see cref="ClientBootstrapSystem.Connect"/> only queues
    /// <c>_LastConnectData</c> / <c>_QueuedConnectRequest</c> and this method is what later
    /// calls <c>FromConnectData</c> + serialize, the prefix runs before the hail is built.
    /// Harmony-safe (no by-ref). CopyBlock/log only when <c>IsBot</c> is actually false,
    /// once per connect attempt (flag reset from the Connect prefix) — never every tick.
    /// </summary>
    private static bool TryBindUpdateConnectRequest()
    {
        try
        {
            if (!HarmonySlotHandoff.TryBindPlugin(
                    HarmonySlotHandoff.UpdateConnectRequest,
                    new UpdateConnectHandler(UpdateConnectHook.Prefix)))
            {
                Line("ClientBootstrapSystem.UpdateConnectRequest slot bind failed; queued-hail IsBot force will not run");
                return false;
            }

            Line("bound ClientBootstrapSystem.UpdateConnectRequest prefix (force ClientConnectData.IsBot=true before hail is used)");
            return true;
        }
        catch (Exception e)
        {
            Line($"ClientBootstrapSystem.UpdateConnectRequest slot bind failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// Poke <c>FakePlatformId</c> and <c>IgnoreSteamInitializationFailure</c> only as the
    /// explicit <see cref="ClientWorldManager.UseFakePlatformId"/> fallback after
    /// <see cref="ClientBootstrapSystem.Connect"/> throws or is missing. Does <em>not</em>
    /// set <c>AllowSkipAuth</c>: that flag makes <c>BeginCreateClientWorlds</c> take
    /// <c>UseFakePlatformId</c> instead of <c>GetAuthSessionTicket</c>, which would skip
    /// the dummy-ticket hail and recreate the §25 null-ticket deny (R5 finding 1).
    /// </summary>
    private static void TryApplyFallbackEditorSettings(string why)
    {
        try
        {
            var editor = SettingsManager.EditorSettings;
            if (editor == null)
            {
                Line($"EditorSettings is null (SettingsManager not initialized yet); not poking FakePlatformId ({why})");
                return;
            }

            var beforeFake = editor.FakePlatformId;
            var beforeIgnore = editor.IgnoreSteamInitializationFailure;
            if (_editorSettingsApplied
                && beforeIgnore
                && beforeFake == _platformId)
            {
                return;
            }

            editor.FakePlatformId = _platformId;
            editor.IgnoreSteamInitializationFailure = true;
            Line(
                $"EditorSettings fallback ({why}): AllowSkipAuth left {editor.AllowSkipAuth} " +
                "(not set: would divert BeginCreateClientWorlds to UseFakePlatformId and drop the dummy-ticket hail, R5 finding 1) " +
                $"FakePlatformId {beforeFake}->{editor.FakePlatformId} " +
                $"IgnoreSteamInitializationFailure {beforeIgnore}->{editor.IgnoreSteamInitializationFailure}");
            _editorSettingsApplied = true;
        }
        catch (Exception e)
        {
            Line($"EditorSettings fallback poke threw: {e}");
        }
    }

    /// <summary>
    /// Write <c>IsBot = true</c> through the interop getter/setter (boxed copy, then
    /// CopyBlock back). <c>_QueuedConnectRequest</c> is <c>Nullable&lt;ClientConnectData&gt;</c>
    /// — a generic over a ValueType wrapper — and is not touched (interop trap 3).
    /// The interop getter never returns managed null (it boxes the inline field).
    /// CopyBlock/log only when <c>IsBot</c> is actually false; skip when PlatformIds /
    /// ticket look unset so a default boxed struct is not written back over a real connect.
    /// <paramref name="oncePerAttempt"/> (UpdateConnectRequest) returns after the first
    /// write this connect attempt so a retry loop cannot log or CopyBlock every tick.
    /// </summary>
    private static void ForceStoredConnectIsBot(ClientBootstrapSystem? bootstrap, string where, bool oncePerAttempt)
    {
        if (bootstrap == null)
        {
            Line($"{where} bootstrap is null; cannot set IsBot");
            return;
        }

        if (oncePerAttempt && _updateConnectWroteThisAttempt)
            return;

        try
        {
            var data = bootstrap._LastConnectData;
            if (data == null)
            {
                Line($"{where} _LastConnectData is null; cannot set IsBot");
                return;
            }

            if (data.IsBot)
                return;

            if (LooksUnset(data))
                return;

            data.IsBot = true;
            bootstrap._LastConnectData = data;
            if (oncePerAttempt)
                _updateConnectWroteThisAttempt = true;
            var readback = bootstrap._LastConnectData != null && bootstrap._LastConnectData.IsBot;
            Line($"hail IsBot=true via {where} readback={readback}");
        }
        catch (Exception e)
        {
            Line($"{where} _LastConnectData.IsBot write threw: {e}");
        }
    }

    /// <summary>
    /// True when the boxed <see cref="ClientConnectData"/> has no ticket and no platform
    /// ids — the default inline field before <see cref="ClientBootstrapSystem.Connect"/>
    /// fills it. CopyBlock of that box would wipe a later real connect.
    /// </summary>
    private static bool LooksUnset(ClientConnectData data)
    {
        try
        {
            var ticket = data.SessionTicket;
            if (ticket != null && ticket.Length > 0)
                return false;
        }
        catch
        {
            /* treat as unset */
        }

        try
        {
            var ids = data.PlatformIds;
            if (ids != null && (ids._PlatformIdCount > 0 || ids._HasSteamUserPlatformId))
                return false;
        }
        catch
        {
            /* treat as unset */
        }

        return true;
    }

    private static void Line(string message)
    {
        try { Plugin.Logger?.LogInfo($"{LogPrefix} {message}"); }
        catch { /* logger can be gone during Unload */ }
    }

    /// <summary>
    /// Skip <see cref="ClientWorldManager.GetAuthSessionTicket"/> so it never calls
    /// <c>PlatformSystemBase.GetAuthSessionTicket(bool, ref SteamNetworkingIdentity, Action)</c>.
    /// The identity parameter is omitted on purpose: it is an <c>Il2CppSystem.ValueType</c>
    /// wrapper and Harmony boxing it is the same family of crash as the by-ref trampoline.
    /// <see cref="ConnectAddress"/> is a real 112-byte C# struct passed by value.
    /// </summary>
    private static class ManagerTicketHook
    {
        public static bool Prefix(
            bool isLAN,
            World clientWorld,
            ConnectAddress connectAddress,
            ConnectAddress fallbackConnectAddress,
            string password)
        {
            try
            {
                Line(
                    $"ClientWorldManager.GetAuthSessionTicket intercepted isLAN={isLAN} instance={_instanceTag} " +
                    $"index={_instanceIndex} platformId={_platformId} world={(clientWorld == null ? "null" : clientWorld.Name)}");
                ContinueConnect(clientWorld, connectAddress, fallbackConnectAddress, password);
            }
            catch (Exception e)
            {
                Line($"ClientWorldManager.GetAuthSessionTicket prefix threw: {e}");
            }

            return false;
        }
    }

    /// <summary>
    /// LAN success continuation: <see cref="ClientBootstrapSystem.Connect"/> with a
    /// <see cref="PlatformIdCollection"/> holding our fake Steam id and a dummy 64-byte
    /// ticket. That is the method the compiler-generated
    /// <c>ClientWorldManager.&lt;&gt;c__DisplayClass14_0._GetAuthSessionTicket_b__0</c>
    /// feeds (CallerCount 2 on Connect: ticket-success and <see cref="ClientWorldManager.UseFakePlatformId"/>).
    /// <see cref="ClientWorldManager.UseFakePlatformId"/> is a private static method, not a
    /// toggle; it is the fallback when Connect cannot run so the extra process still does
    /// not touch Steam. The id it writes is a debug value we do not control.
    /// </summary>
    private static void ContinueConnect(
        World? clientWorld,
        ConnectAddress connectAddress,
        ConnectAddress fallbackConnectAddress,
        string password)
    {
        var platformId = _platformId;
        var pwd = password ?? "";

        if (clientWorld == null || !clientWorld.IsCreated)
        {
            Line("clientWorld missing or destroyed; cannot Connect. Hail will not start.");
            return;
        }

        try
        {
            var bootstrap = clientWorld.GetExistingSystemManaged<ClientBootstrapSystem>();
            if (bootstrap == null)
            {
                Line("ClientBootstrapSystem missing on client world; falling back to UseFakePlatformId");
                FallbackUseFakePlatformId(clientWorld, connectAddress, fallbackConnectAddress, pwd, "ClientBootstrapSystem missing");
                return;
            }

            var ids = new PlatformIdCollection();
            SteamUserPlatformId steamId = default;
            steamId._Id = platformId;
            ids.Add(steamId);
            // OnConnectionApproval uses Steam or PSN; EOS-only logs "Platform ID missing!"
            // and disconnects reason 0x11 (native OnConnectionApproval.c:307-327).
            Line(
                $"PlatformIdCollection.Add SteamUserPlatformId._Id={platformId} " +
                $"count={ids._PlatformIdCount} hasSteam={ids._HasSteamUserPlatformId}");

            // Verified live 2026-09-03 (evidence §25): the LAN server denies a NULL/empty ticket even with
            // Authenticate=false ("connection was denied because of null ticket"), so send a dummy
            // non-empty ticket; with Authenticate=false the contents are not validated (inferred).
            Il2CppStructArray<byte> ticket;
            try { ticket = new Il2CppStructArray<byte>(64); for (int i = 0; i < 64; i++) ticket[i] = (byte)(0x50 + (i % 16)); }
            catch (Exception e)
            {
                Line($"empty ticket array threw ({e.Message}); using a 1-byte zero buffer");
                ticket = new Il2CppStructArray<byte>(1);
                ticket[0] = 0;
            }

            Line($"calling ClientBootstrapSystem.Connect ticketBytes={ticket.Length} platformId={platformId}");
            bootstrap.Connect(connectAddress, fallbackConnectAddress, ids, ticket, pwd);
            Line("ClientBootstrapSystem.Connect returned");
        }
        catch (Exception e)
        {
            Line($"ClientBootstrapSystem.Connect threw ({e}); falling back to UseFakePlatformId");
            FallbackUseFakePlatformId(clientWorld, connectAddress, fallbackConnectAddress, pwd, "ClientBootstrapSystem.Connect threw");
        }
    }

    private static void FallbackUseFakePlatformId(
        World clientWorld,
        ConnectAddress connectAddress,
        ConnectAddress fallbackConnectAddress,
        string password,
        string why)
    {
        try
        {
            Line(
                $"UseFakePlatformId fallback ({why}) instance={_instanceTag} index={_instanceIndex} " +
                $"wantedPlatformId={_platformId} (game picks its own FakePlatformId unless EditorSettings.FakePlatformId stuck; watch for 'Faking')");
            TryApplyFallbackEditorSettings(why);
            ClientWorldManager.UseFakePlatformId(clientWorld, connectAddress, fallbackConnectAddress, password);
            Line("UseFakePlatformId returned");
        }
        catch (Exception e)
        {
            Line($"UseFakePlatformId threw: {e}");
        }
    }

    private static class ConnectIsBotHook
    {
        public static void Prefix()
        {
            try
            {
                Line("ClientBootstrapSystem.Connect prefix");
                _updateConnectWroteThisAttempt = false;
            }
            catch (Exception e)
            {
                Line($"ClientBootstrapSystem.Connect prefix threw: {e}");
            }
        }

        public static void Postfix(ClientBootstrapSystem __instance)
        {
            try
            {
                ForceStoredConnectIsBot(__instance, "ClientBootstrapSystem.Connect postfix", oncePerAttempt: false);
            }
            catch (Exception e)
            {
                Line($"ClientBootstrapSystem.Connect postfix threw: {e}");
            }
        }
    }

    private static class UpdateConnectHook
    {
        public static void Prefix(ClientBootstrapSystem __instance)
        {
            try
            {
                ForceStoredConnectIsBot(__instance, "UpdateConnectRequest prefix", oncePerAttempt: true);
            }
            catch (Exception e)
            {
                Line($"UpdateConnectRequest prefix threw: {e}");
            }
        }
    }

    /// <summary>
    /// Extra-instance <c>connect</c> still calls <see cref="ConnectAddress.CreateSteamIPv4"/>
    /// (ConnectOps is not edited). SteamIPv4 is Steam Networking Sockets; the extra process
    /// already failed <c>SteamAPI.Init</c>. Rewrite the returned address to Lidgren using the
    /// same host/port the factory was given — do not call <c>ConnectAddress.ToString()</c>
    /// (interop ToString goes through a generic-looking invoke; the host/port args are enough).
    /// </summary>
    private static class AddressHook
    {
        public static void Postfix(string address, ushort port, ref ConnectAddress __result)
        {
            try
            {
                if (__result.Protocol == TransportLayerProtocol.LidgrenIPv4)
                    return;

                var rewritten = ConnectAddress.CreateLidgrenIPv4(address, port);
                __result = rewritten;
                Line($"rewrote SteamIPv4://{address}:{port} -> Lidgren://{address}:{port}");
            }
            catch (Exception e)
            {
                Line($"CreateSteamIPv4 postfix threw: {e}");
            }
        }
    }
}
