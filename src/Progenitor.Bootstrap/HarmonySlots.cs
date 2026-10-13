using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using ProjectM;
using ProjectM.Network;
using Stunlock.Network;
using Unity.Collections;
using Unity.Entities;
using GameLocalization = Stunlock.Localization.Localization;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime typed slots for every Core Harmony patch. Bootstrap's patch bodies
/// marshal Harmony arguments, then call through these delegates. Core installs at
/// <c>Install</c> and clears at <c>Uninstall</c> (ReceiveDetours pattern). Null slot =
/// vanilla original.
/// </summary>
internal static class HarmonySlots
{
    // The four cross-ALC update slot delegates. These declarations must stay CHARACTER-IDENTICAL
    // to the ones in Progenitor.Core's SystemHooks: Bind below joins them with
    // Delegate.CreateDelegate, which throws on any skew and takes the site Inert (FLATCH1 measured
    // that). A test pins the two texts against each other. Job FHOOK3 widened them from
    // `bool hasAfter` to `long callToken` so Core's postfix can release the frame THIS call pushed
    // rather than whatever is on top of its stack; zero still means "the prefix pushed nothing",
    // so the old bit is not lost. A widening here is a Bootstrap<->Core contract change and must
    // bump CoreLayout.ProtocolVersion / BootstrapLink.ProtocolVersion in the same change.
    public delegate bool ManagedUpdatePrefix(SystemBase instance, ref long callToken);
    public delegate void ManagedUpdatePostfix(long callToken);
    public delegate bool UnmanagedUpdatePrefix(IntPtr pSystemState, ref long callToken);
    public delegate void UnmanagedUpdatePostfix(long callToken);
    public delegate void ChatOnUpdate(ChatMessageSystem instance);
    public delegate void UserConnected(ServerBootstrapSystem instance, NetConnectionId netConnectionId);
    public delegate void UserDisconnected(ServerBootstrapSystem instance, NetConnectionId netConnectionId, string reason, string extra);
    public delegate void DeserializeLoadFile();
    public delegate void TriggerSave(bool result);
    public delegate void BeginCreateServerWorld(string? saveDirectoryPath, string? saveToLoad);
    public delegate bool StunProcessStart(string? fileName, string? dir, out bool enteredNest);
    public delegate void StunProcessStartFinalizer(bool enteredNest);
    public delegate void SetLanguage(string language);
    public delegate void LaunchGame(GameConnect launchData);
    public delegate void GameBootstrapInstance(GameBootstrap instance);
    public delegate void SendPacketToUser(NativeArray<byte> data, int lengthBits, int userIndex, bool reliable, bool isConnectedUserIndex, string result);
    public delegate bool GetAuthSessionTicket(bool isLAN, World clientWorld, ConnectAddress connectAddress, ConnectAddress fallbackConnectAddress, string password);
    public delegate void CreateSteamIPv4(string address, ushort port, ref ConnectAddress result);
    public delegate void ConnectPrefix();
    public delegate void ConnectPostfix(ClientBootstrapSystem instance);
    public delegate void UpdateConnectRequest(ClientBootstrapSystem instance);
    public delegate bool Authenticate(ServerBootstrapSystem instance, NetConnectionId netConnectionId, ulong platformUserId, string eosId, int usersTaken, int maxConnectedUsers, bool connectedAsAdmin);

    private static ManagedUpdatePrefix? _managedPrefix;
    private static ManagedUpdatePostfix? _managedPostfix;
    private static UnmanagedUpdatePrefix? _unmanagedPrefix;
    private static UnmanagedUpdatePostfix? _unmanagedPostfix;
    private static ChatOnUpdate? _chatOnUpdate;
    private static UserConnected? _userConnected;
    private static UserDisconnected? _userDisconnected;
    private static DeserializeLoadFile? _deserializePrefix;
    private static DeserializeLoadFile? _deserializePostfix;
    private static TriggerSave? _triggerSave;
    private static BeginCreateServerWorld? _beginCreate;
    private static StunProcessStart? _stunPrefix;
    private static StunProcessStartFinalizer? _stunFinalizer;
    private static SetLanguage? _setLanguage;
    private static LaunchGame? _launchGame;
    private static GameBootstrapInstance? _gameBootstrapStart;
    private static GameBootstrapInstance? _gameBootstrapAwake;
    private static SendPacketToUser? _sendPacketToUser;
    private static GetAuthSessionTicket? _getAuthSessionTicket;
    private static CreateSteamIPv4? _createSteamIPv4;
    private static ConnectPrefix? _connectPrefix;
    private static ConnectPostfix? _connectPostfix;
    private static UpdateConnectRequest? _updateConnectRequest;
    private static Authenticate? _authenticate;

    private static readonly HashSet<string> PatchedSites = new(StringComparer.Ordinal);
    private static readonly object OwnerGate = new();
    private static readonly Dictionary<string, string> PluginOwners = new(StringComparer.Ordinal);

    private static ManagedUpdatePrefix? _heldManagedPrefix;
    private static ManagedUpdatePostfix? _heldManagedPostfix;
    private static UnmanagedUpdatePrefix? _heldUnmanagedPrefix;
    private static UnmanagedUpdatePostfix? _heldUnmanagedPostfix;
    private static ChatOnUpdate? _heldChat;
    private static UserConnected? _heldConnected;
    private static UserDisconnected? _heldDisconnected;
    private static DeserializeLoadFile? _heldDeserializePrefix;
    private static DeserializeLoadFile? _heldDeserializePostfix;
    private static TriggerSave? _heldTriggerSave;
    private static BeginCreateServerWorld? _heldBeginCreate;
    private static StunProcessStart? _heldStunPrefix;
    private static StunProcessStartFinalizer? _heldStunFinalizer;
    private static SetLanguage? _heldLanguage;
    private static bool _holding;

    /// <summary>
    /// True when Bootstrap patched at least one content site. False when every
    /// <c>TryPatch*</c> returned 0 so Core may fallback-<c>PatchAll</c>.
    /// </summary>
    public static bool OwnsPatches { get; private set; }

    public static ManagedUpdatePrefix? ManagedPrefix => Volatile.Read(ref _managedPrefix);
    public static ManagedUpdatePostfix? ManagedPostfix => Volatile.Read(ref _managedPostfix);
    public static UnmanagedUpdatePrefix? UnmanagedPrefix => Volatile.Read(ref _unmanagedPrefix);
    public static UnmanagedUpdatePostfix? UnmanagedPostfix => Volatile.Read(ref _unmanagedPostfix);
    public static ChatOnUpdate? Chat => Volatile.Read(ref _chatOnUpdate);
    public static UserConnected? Connected => Volatile.Read(ref _userConnected);
    public static UserDisconnected? Disconnected => Volatile.Read(ref _userDisconnected);
    public static DeserializeLoadFile? DeserializePrefix => Volatile.Read(ref _deserializePrefix);
    public static DeserializeLoadFile? DeserializePostfix => Volatile.Read(ref _deserializePostfix);
    public static TriggerSave? TriggerSaveSlot => Volatile.Read(ref _triggerSave);
    public static BeginCreateServerWorld? BeginCreate => Volatile.Read(ref _beginCreate);
    public static StunProcessStart? StunPrefix => Volatile.Read(ref _stunPrefix);
    public static StunProcessStartFinalizer? StunFinalizer => Volatile.Read(ref _stunFinalizer);
    public static SetLanguage? Language => Volatile.Read(ref _setLanguage);
    public static LaunchGame? LaunchGameSlot => Volatile.Read(ref _launchGame);
    public static GameBootstrapInstance? BootstrapStart => Volatile.Read(ref _gameBootstrapStart);
    public static GameBootstrapInstance? BootstrapAwake => Volatile.Read(ref _gameBootstrapAwake);
    public static SendPacketToUser? SendToUser => Volatile.Read(ref _sendPacketToUser);
    public static GetAuthSessionTicket? AuthTicket => Volatile.Read(ref _getAuthSessionTicket);
    public static CreateSteamIPv4? SteamAddress => Volatile.Read(ref _createSteamIPv4);
    public static ConnectPrefix? ConnectPre => Volatile.Read(ref _connectPrefix);
    public static ConnectPostfix? ConnectPost => Volatile.Read(ref _connectPostfix);
    public static UpdateConnectRequest? UpdateConnect => Volatile.Read(ref _updateConnectRequest);
    public static Authenticate? AuthenticateSlot => Volatile.Read(ref _authenticate);

    /// <summary>
    /// True when Bootstrap's process-lifetime trampoline for this original is live.
    /// Slot names (prefix/postfix) map onto the AllSites original.
    /// </summary>
    public static bool OwnsSite(string? site)
    {
        if (!OwnsPatches || string.IsNullOrEmpty(site)) return false;
        string key = HarmonySlotPolicy.CanonicalSite(site);
        return PatchedSites.Contains(key);
    }

    /// <summary>Record a successful Bootstrap patch. No-op for an empty name.</summary>
    internal static void MarkPatched(string site)
    {
        if (string.IsNullOrEmpty(site)) return;
        PatchedSites.Add(HarmonySlotPolicy.CanonicalSite(site));
        OwnsPatches = true;
    }

    /// <summary>
    /// Snapshot live Core forwards so Uninstall's <see cref="ClearAll"/> does not leave
    /// ECS running vanilla until <c>ALC.Unload</c>. Restore after Uninstall; release
    /// immediately before Unload so the collectible delegates are not a collect pin.
    /// </summary>
    internal static void HoldForUnload()
    {
        _heldManagedPrefix = Volatile.Read(ref _managedPrefix);
        _heldManagedPostfix = Volatile.Read(ref _managedPostfix);
        _heldUnmanagedPrefix = Volatile.Read(ref _unmanagedPrefix);
        _heldUnmanagedPostfix = Volatile.Read(ref _unmanagedPostfix);
        _heldChat = Volatile.Read(ref _chatOnUpdate);
        _heldConnected = Volatile.Read(ref _userConnected);
        _heldDisconnected = Volatile.Read(ref _userDisconnected);
        _heldDeserializePrefix = Volatile.Read(ref _deserializePrefix);
        _heldDeserializePostfix = Volatile.Read(ref _deserializePostfix);
        _heldTriggerSave = Volatile.Read(ref _triggerSave);
        _heldBeginCreate = Volatile.Read(ref _beginCreate);
        _heldStunPrefix = Volatile.Read(ref _stunPrefix);
        _heldStunFinalizer = Volatile.Read(ref _stunFinalizer);
        _heldLanguage = Volatile.Read(ref _setLanguage);
        _holding = true;
    }

    /// <summary>Put the held Core forwards back after Uninstall <see cref="ClearAll"/>.</summary>
    internal static void RestoreHeld()
    {
        if (!_holding) return;
        Volatile.Write(ref _managedPrefix, _heldManagedPrefix);
        Volatile.Write(ref _managedPostfix, _heldManagedPostfix);
        Volatile.Write(ref _unmanagedPrefix, _heldUnmanagedPrefix);
        Volatile.Write(ref _unmanagedPostfix, _heldUnmanagedPostfix);
        Volatile.Write(ref _chatOnUpdate, _heldChat);
        Volatile.Write(ref _userConnected, _heldConnected);
        Volatile.Write(ref _userDisconnected, _heldDisconnected);
        Volatile.Write(ref _deserializePrefix, _heldDeserializePrefix);
        Volatile.Write(ref _deserializePostfix, _heldDeserializePostfix);
        Volatile.Write(ref _triggerSave, _heldTriggerSave);
        Volatile.Write(ref _beginCreate, _heldBeginCreate);
        Volatile.Write(ref _stunPrefix, _heldStunPrefix);
        Volatile.Write(ref _stunFinalizer, _heldStunFinalizer);
        Volatile.Write(ref _setLanguage, _heldLanguage);
    }

    /// <summary>
    /// Drop held forwards and null the live slots. Call immediately before
    /// <c>ALC.Unload</c> so collect is not pinned by these delegates.
    /// </summary>
    internal static void ReleaseHeld()
    {
        _holding = false;
        _heldManagedPrefix = null;
        _heldManagedPostfix = null;
        _heldUnmanagedPrefix = null;
        _heldUnmanagedPostfix = null;
        _heldChat = null;
        _heldConnected = null;
        _heldDisconnected = null;
        _heldDeserializePrefix = null;
        _heldDeserializePostfix = null;
        _heldTriggerSave = null;
        _heldBeginCreate = null;
        _heldStunPrefix = null;
        _heldStunFinalizer = null;
        _heldLanguage = null;
        ClearAll();
    }

    /// <summary>
    /// Point one site at Core's handler, or null it. Returns false when Bootstrap did not
    /// patch that original (so Core may patch itself for that site only) or the site is unknown.
    /// Plugin sites may pass <paramref name="owner"/>; a different owner is refused.
    /// </summary>
    public static bool TrySet(string site, Delegate? dispatcher) =>
        TrySet(site, dispatcher, owner: null);

    public static bool TrySet(string site, Delegate? dispatcher, string? owner)
    {
        if (string.IsNullOrEmpty(site)) return false;
        if (dispatcher != null && HarmonySlotPolicy.IsLazySite(site))
            HarmonyPatches.EnsureLazy(site);
        if (!OwnsSite(site)) return false;
        if (HarmonySlotPolicy.IsPluginSlot(site) && !TryClaimPluginOwner(site, dispatcher, owner))
            return false;
        try
        {
            switch (site)
            {
                case HarmonySlotPolicy.SystemBaseUpdatePrefix:
                    Volatile.Write(ref _managedPrefix, Bind<ManagedUpdatePrefix>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.SystemBaseUpdatePostfix:
                    Volatile.Write(ref _managedPostfix, Bind<ManagedUpdatePostfix>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.UnmanagedUpdatePrefix:
                    Volatile.Write(ref _unmanagedPrefix, Bind<UnmanagedUpdatePrefix>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.UnmanagedUpdatePostfix:
                    Volatile.Write(ref _unmanagedPostfix, Bind<UnmanagedUpdatePostfix>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.ChatOnUpdate:
                    Volatile.Write(ref _chatOnUpdate, Bind<ChatOnUpdate>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.UserConnected:
                    Volatile.Write(ref _userConnected, Bind<UserConnected>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.UserDisconnected:
                    Volatile.Write(ref _userDisconnected, Bind<UserDisconnected>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.DeserializeLoadFilePrefix:
                    Volatile.Write(ref _deserializePrefix, Bind<DeserializeLoadFile>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.DeserializeLoadFilePostfix:
                    Volatile.Write(ref _deserializePostfix, Bind<DeserializeLoadFile>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.TriggerSave:
                    Volatile.Write(ref _triggerSave, Bind<TriggerSave>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.BeginCreateServerWorld:
                    Volatile.Write(ref _beginCreate, Bind<BeginCreateServerWorld>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.StunProcessStartPrefix:
                    Volatile.Write(ref _stunPrefix, Bind<StunProcessStart>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.StunProcessStartFinalizer:
                    Volatile.Write(ref _stunFinalizer, Bind<StunProcessStartFinalizer>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.SetLanguage:
                    Volatile.Write(ref _setLanguage, Bind<SetLanguage>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.LaunchGame:
                    Volatile.Write(ref _launchGame, BindPlugin<LaunchGame>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.GameBootstrapStart:
                    Volatile.Write(ref _gameBootstrapStart, BindPlugin<GameBootstrapInstance>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.GameBootstrapAwake:
                    Volatile.Write(ref _gameBootstrapAwake, BindPlugin<GameBootstrapInstance>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.SendPacketToUser:
                    Volatile.Write(ref _sendPacketToUser, BindPlugin<SendPacketToUser>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.GetAuthSessionTicket:
                    Volatile.Write(ref _getAuthSessionTicket, BindPlugin<GetAuthSessionTicket>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.CreateSteamIPv4:
                    Volatile.Write(ref _createSteamIPv4, BindPlugin<CreateSteamIPv4>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.ClientBootstrapConnectPrefix:
                    Volatile.Write(ref _connectPrefix, BindPlugin<ConnectPrefix>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.ClientBootstrapConnectPostfix:
                    Volatile.Write(ref _connectPostfix, BindPlugin<ConnectPostfix>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.UpdateConnectRequest:
                    Volatile.Write(ref _updateConnectRequest, BindPlugin<UpdateConnectRequest>(dispatcher, site));
                    return true;
                case HarmonySlotPolicy.Authenticate:
                    Volatile.Write(ref _authenticate, BindPlugin<Authenticate>(dispatcher, site));
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"HarmonySlots: bind '{site}' threw: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Null every plugin slot bound by <paramref name="owner"/>. ReloaderHost
    /// Unload and Core Uninstall both call this so a missed Dev <c>ClearPlugin</c>
    /// cannot pin the plugin ALC across a Core swap.
    /// </summary>
    public static int ClearOwned(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return 0;
        string[] sites = HarmonySlotPolicy.PluginSlotNames;
        int n = 0;
        for (int i = 0; i < sites.Length; i++)
        {
            string site = sites[i];
            string? current;
            lock (OwnerGate)
                PluginOwners.TryGetValue(site, out current);
            if (!HarmonySlotPolicy.OwnerMatchesUnload(current, owner, owner))
                continue;
            if (TrySet(site, null, owner))
                n++;
        }
        if (n > 0)
            ProcessHost.Log?.LogInfo(HarmonySlotPolicy.FormatClearedOwned(owner, n));
        return n;
    }

    /// <summary>
    /// Null plugin slots whose dispatcher method or target lives in the unloading
    /// assembly / ALC, then any remaining slots owned by those names.
    /// </summary>
    public static int ClearPluginContext(Assembly? assembly, string? alcName)
    {
        int n = 0;
        string? assemblyName = null;
        try { assemblyName = assembly?.GetName().Name; }
        catch { assemblyName = null; }

        string[] sites = HarmonySlotPolicy.PluginSlotNames;
        for (int i = 0; i < sites.Length; i++)
        {
            string site = sites[i];
            Delegate? live = ReadPluginDispatcher(site);
            if (DispatcherBelongs(live, assembly, alcName))
            {
                if (TrySet(site, null, owner: null))
                    n++;
                continue;
            }
            string? current;
            lock (OwnerGate)
                PluginOwners.TryGetValue(site, out current);
            if (HarmonySlotPolicy.OwnerMatchesUnload(current, alcName, assemblyName))
            {
                if (TrySet(site, null, current))
                    n++;
            }
        }
        string label = !string.IsNullOrEmpty(alcName) ? alcName : (assemblyName ?? "");
        if (n > 0 && !string.IsNullOrEmpty(label))
            ProcessHost.Log?.LogInfo(HarmonySlotPolicy.FormatClearedOwned(label, n));
        return n;
    }

    public static void ClearAll()
    {
        Volatile.Write(ref _managedPrefix, null);
        Volatile.Write(ref _managedPostfix, null);
        Volatile.Write(ref _unmanagedPrefix, null);
        Volatile.Write(ref _unmanagedPostfix, null);
        Volatile.Write(ref _chatOnUpdate, null);
        Volatile.Write(ref _userConnected, null);
        Volatile.Write(ref _userDisconnected, null);
        Volatile.Write(ref _deserializePrefix, null);
        Volatile.Write(ref _deserializePostfix, null);
        Volatile.Write(ref _triggerSave, null);
        Volatile.Write(ref _beginCreate, null);
        Volatile.Write(ref _stunPrefix, null);
        Volatile.Write(ref _stunFinalizer, null);
        Volatile.Write(ref _setLanguage, null);
        Volatile.Write(ref _launchGame, null);
        Volatile.Write(ref _gameBootstrapStart, null);
        Volatile.Write(ref _gameBootstrapAwake, null);
        Volatile.Write(ref _sendPacketToUser, null);
        Volatile.Write(ref _getAuthSessionTicket, null);
        Volatile.Write(ref _createSteamIPv4, null);
        Volatile.Write(ref _connectPrefix, null);
        Volatile.Write(ref _connectPostfix, null);
        Volatile.Write(ref _updateConnectRequest, null);
        Volatile.Write(ref _authenticate, null);
        lock (OwnerGate) PluginOwners.Clear();
    }

    private static bool TryClaimPluginOwner(string site, Delegate? dispatcher, string? owner)
    {
        if (dispatcher != null && string.IsNullOrEmpty(owner))
            owner = InferOwner(dispatcher);
        lock (OwnerGate)
        {
            PluginOwners.TryGetValue(site, out var current);
            if (dispatcher == null)
            {
                if (!HarmonySlotPolicy.MayClearPlugin(current, owner))
                {
                    ProcessHost.Log?.LogWarning(
                        HarmonySlotPolicy.FormatClearOwnerRefused(site, current ?? "", owner ?? ""));
                    return false;
                }
                PluginOwners.Remove(site);
                return true;
            }
            if (string.IsNullOrEmpty(owner))
            {
                ProcessHost.Log?.LogWarning(HarmonySlotPolicy.FormatEmptyOwnerRefused(site));
                return false;
            }
            if (!HarmonySlotPolicy.MayBindPlugin(current, owner))
            {
                ProcessHost.Log?.LogWarning(
                    HarmonySlotPolicy.FormatOwnerRefused(site, current ?? "", owner ?? ""));
                return false;
            }
            PluginOwners[site] = owner;
            return true;
        }
    }

    private static string InferOwner(Delegate dispatcher)
    {
        try
        {
            var asm = dispatcher.Method?.DeclaringType?.Assembly
                ?? dispatcher.Target?.GetType().Assembly;
            if (asm == null) return "";
            try
            {
                var alc = AssemblyLoadContext.GetLoadContext(asm);
                if (!string.IsNullOrEmpty(alc?.Name)) return alc.Name;
            }
            catch { /* unloading */ }
            return asm.GetName().Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static Delegate? ReadPluginDispatcher(string site)
    {
        switch (site)
        {
            case HarmonySlotPolicy.LaunchGame: return LaunchGameSlot;
            case HarmonySlotPolicy.GameBootstrapStart: return BootstrapStart;
            case HarmonySlotPolicy.GameBootstrapAwake: return BootstrapAwake;
            case HarmonySlotPolicy.SendPacketToUser: return SendToUser;
            case HarmonySlotPolicy.GetAuthSessionTicket: return AuthTicket;
            case HarmonySlotPolicy.CreateSteamIPv4: return SteamAddress;
            case HarmonySlotPolicy.ClientBootstrapConnectPrefix: return ConnectPre;
            case HarmonySlotPolicy.ClientBootstrapConnectPostfix: return ConnectPost;
            case HarmonySlotPolicy.UpdateConnectRequest: return UpdateConnect;
            case HarmonySlotPolicy.Authenticate: return AuthenticateSlot;
            default: return null;
        }
    }

    private static bool DispatcherBelongs(Delegate? dispatcher, Assembly? assembly, string? alcName)
    {
        if (dispatcher == null) return false;
        try
        {
            var methodAsm = dispatcher.Method?.DeclaringType?.Assembly;
            if (assembly != null && methodAsm != null && ReferenceEquals(methodAsm, assembly))
                return true;
            if (BelongsToAlc(methodAsm, alcName)) return true;
        }
        catch { /* trampoline .Method */ }
        try
        {
            var target = dispatcher.Target;
            if (target == null) return false;
            var targetAsm = target.GetType().Assembly;
            if (assembly != null && ReferenceEquals(targetAsm, assembly))
                return true;
            if (BelongsToAlc(targetAsm, alcName)) return true;
        }
        catch { /* target type */ }
        return false;
    }

    private static bool BelongsToAlc(Assembly? assembly, string? alcName)
    {
        if (assembly == null || string.IsNullOrEmpty(alcName)) return false;
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(assembly);
            return alc != null && string.Equals(alc.Name, alcName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static T? Bind<T>(Delegate? dispatcher, string site) where T : Delegate
    {
        if (dispatcher == null) return null;
        T bound = dispatcher is T typed
            ? typed
            : (T)Delegate.CreateDelegate(typeof(T), dispatcher.Target, dispatcher.Method);
        NativePinRegistry.KeepDelegate(bound, "harmony." + site, CoreLoader.CurrentPinGeneration);
        return bound;
    }

    /// <summary>
    /// Plugin slots must not <see cref="NativePinRegistry.KeepDelegate"/> under a Core
    /// generation: that pin outlives plugin Unload and is the evidence-66 path. The
    /// Bootstrap field is the only root; <see cref="ClearAll"/> / a null bind drops it.
    /// </summary>
    private static T? BindPlugin<T>(Delegate? dispatcher, string site) where T : Delegate
    {
        _ = site;
        if (dispatcher == null) return null;
        return dispatcher is T typed
            ? typed
            : (T)Delegate.CreateDelegate(typeof(T), dispatcher.Target, dispatcher.Method);
    }
}
