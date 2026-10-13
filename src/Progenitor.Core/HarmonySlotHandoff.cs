using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Progenitor;

/// <summary>
/// Writes Core's Harmony handlers into Bootstrap's process-lifetime slots and clears them
/// on uninstall. This file is the collectible production path: no HarmonyLib types. When
/// Bootstrap owns a site, Core fills that slot. When a site was not patched, Core may
/// fallback-<c>Patch</c> that site only (HarmonyLib lives in <c>Reload/HarmonyFallback.cs</c>).
/// The diagnostic skip installs none of the ten content sites so a dry-run <c>core_reload</c>
/// can test whether Harmony IL2CPP trampolines pin the collectible generation; tick stays
/// on Bootstrap and skip is refused on a live world unless a second flag confirms.
/// Plugin / extra-instance IL2CPP patches use the same slots via
/// <see cref="TryBindPlugin"/>: a collectible plugin must not Harmony-patch an IL2CPP
/// method from its own assembly (the trampoline cache pins the ALC and Core with it).
/// </summary>
public static class HarmonySlotHandoff
{
    public const int SiteCount = 11;

    public const string SystemBaseUpdate = "SystemBase.Update";
    public const string UnmanagedUpdate = "WorldUnmanagedImpl.UnmanagedUpdate";
    public const string ChatOnUpdate = "ChatMessageSystem.OnUpdate";
    public const string UserConnected = "ServerBootstrapSystem.OnUserConnected";
    public const string UserDisconnected = "ServerBootstrapSystem.OnUserDisconnected";
    public const string DeserializeLoadFile = "LoadPersistenceSystemV2.DeserializeLoadFile";
    public const string TriggerSave = "TriggerPersistenceSaveSystem.TriggerSave";
    public const string BeginCreateServerWorld = "ServerWorldManager.BeginCreateServerWorld";
    public const string StunProcessStart = "StunProcess.Start";
    public const string SetLanguage = "GameLocalization.SetLanguage";
    public const string BootstrapSystemOnUpdate = "BootstrapSystem.OnUpdate";

    public const string SystemBaseUpdatePrefix = "SystemBase.Update.prefix";
    public const string SystemBaseUpdatePostfix = "SystemBase.Update.postfix";
    public const string UnmanagedUpdatePrefix = "WorldUnmanagedImpl.UnmanagedUpdate.prefix";
    public const string UnmanagedUpdatePostfix = "WorldUnmanagedImpl.UnmanagedUpdate.postfix";
    public const string DeserializeLoadFilePrefix = "LoadPersistenceSystemV2.DeserializeLoadFile.prefix";
    public const string DeserializeLoadFilePostfix = "LoadPersistenceSystemV2.DeserializeLoadFile.postfix";
    public const string StunProcessStartPrefix = "StunProcess.Start.prefix";
    public const string StunProcessStartFinalizer = "StunProcess.Start.finalizer";

    public const string LaunchGame = "GameHelper.LaunchGame";
    public const string GameBootstrapStart = "GameBootstrap.Start";
    public const string GameBootstrapAwake = "GameBootstrap.Awake";
    public const string SendPacketToUser = "ServerBootstrapSystem.SendPacketToUser";
    public const string GetAuthSessionTicket = "ClientWorldManager.GetAuthSessionTicket";
    public const string CreateSteamIPv4 = "ConnectAddress.CreateSteamIPv4";
    public const string ClientBootstrapConnect = "ClientBootstrapSystem.Connect";
    public const string ClientBootstrapConnectPrefix = "ClientBootstrapSystem.Connect.prefix";
    public const string ClientBootstrapConnectPostfix = "ClientBootstrapSystem.Connect.postfix";
    public const string UpdateConnectRequest = "ClientBootstrapSystem.UpdateConnectRequest";
    public const string Authenticate = "ServerBootstrapSystem.Authenticate";

    /// <summary>
    /// Bootstrap late-frame slot (not a Harmony site). Core fills an <c>Action</c> at
    /// Effects.Install; Bootstrap's camera callback / FrameTicker LateUpdate invokes it.
    /// </summary>
    public const string LateFrame = "LateFrame";

    public static readonly string[] AllSites =
    {
        SystemBaseUpdate,
        UnmanagedUpdate,
        ChatOnUpdate,
        UserConnected,
        UserDisconnected,
        DeserializeLoadFile,
        TriggerSave,
        BeginCreateServerWorld,
        StunProcessStart,
        SetLanguage,
        BootstrapSystemOnUpdate,
    };

    public static readonly string[] ContentSites =
    {
        SystemBaseUpdate,
        UnmanagedUpdate,
        ChatOnUpdate,
        UserConnected,
        UserDisconnected,
        DeserializeLoadFile,
        TriggerSave,
        BeginCreateServerWorld,
        StunProcessStart,
        SetLanguage,
    };

    public static readonly string[] PluginSites =
    {
        LaunchGame,
        GameBootstrapStart,
        GameBootstrapAwake,
        SendPacketToUser,
        GetAuthSessionTicket,
        CreateSteamIPv4,
        ClientBootstrapConnect,
        UpdateConnectRequest,
        Authenticate,
    };

    public static readonly string[] PluginSlotNames =
    {
        LaunchGame,
        GameBootstrapStart,
        GameBootstrapAwake,
        SendPacketToUser,
        GetAuthSessionTicket,
        CreateSteamIPv4,
        ClientBootstrapConnectPrefix,
        ClientBootstrapConnectPostfix,
        UpdateConnectRequest,
        Authenticate,
    };

    public const string FallbackLog = "Bootstrap Harmony slots absent: Core applying Harmony patches itself.";

    public static string SkipLog(string site) =>
        "Diagnostics.SkipCoreHarmony: skipped Harmony site " + site;

    public static string SkipVanillaLog(string site) =>
        SkipLog(site) + " — " + SkipSiteEffect(site);

    public static string SkipEnabledLog =>
        "Diagnostics.SkipCoreHarmony=true: Core will install none of its Harmony patches. Tick stays (Bootstrap).";

    public static string SkipTickStaysLog =>
        "Diagnostics.SkipCoreHarmony: tick stays (Bootstrap).";

    public static string SkipRefusedLiveWorldLog =>
        "Diagnostics.SkipCoreHarmony refused: a world is live; set Diagnostics.SkipCoreHarmonyAllowLiveWorld=true to confirm a vanilla dry-run against this save.";

    public static string SkipSiteEffect(string site)
    {
        switch (site)
        {
            case SystemBaseUpdate:
            case UnmanagedUpdate:
                return "Before/After and ctx.Systems.Add do not run. Frame waiters still do.";
            case ChatOnUpdate:
                return "No ChatMessage. Commands never see .ember / .joins. Dot-text is vanilla chat.";
            case UserConnected:
            case UserDisconnected:
                return "No PlayerConnected / PlayerDisconnected. Prefab PrfD/PrfK not sent on join. Disconnect JSON flush gone.";
            case DeserializeLoadFile:
                return "Custom prefabs not applied before load. A save that already stores a cloned GUID drops it.";
            case TriggerSave:
                return "Autosave does not ModSave.SaveAll. world_save still flushes JSON itself.";
            case BeginCreateServerWorld:
                return "Path capture waits for WorldReady singleton / config fallback.";
            case StunProcessStart:
                return "No staging, no DOORSTOP_* strip. Private Game inherits the client's DOORSTOP_*.";
            case SetLanguage:
                return "First en.json load still works. UI language change wipes mod strings.";
            case BootstrapSystemOnUpdate:
                return "Tick / reload / bridge / core_reload still run (Bootstrap).";
            default:
                return "vanilla";
        }
    }

    public static string FallbackSiteLog(string site) =>
        "Bootstrap Harmony slots absent: Core applying Harmony patch on " + site;

    public static bool CoreMayApplyHarmony(bool hostOwnsSite, bool skipCoreHarmony) =>
        !hostOwnsSite && !skipCoreHarmony;

    public static bool CoreMayFillSlot(bool hostOwnsSite, bool skipCoreHarmony) =>
        hostOwnsSite && !skipCoreHarmony;

    /// <summary>Set when SkipCoreHarmony is on but a live world exists and the confirm flag is off.</summary>
    internal static bool SkipRefusedLiveWorld { get; set; }

#if PROGENITOR_TESTS
    private static ISlot? _override;

    internal static void Use(ISlot? slot)
    {
        _override = slot;
        if (slot == null)
        {
            SkipRefusedLiveWorld = false;
            SetSkipModules(null, refusedLiveWorld: false);
            lock (FillGate)
            {
                _fillGroup = null;
                _fillGroupArmed.Clear();
            }
        }
    }

    internal interface ISlot
    {
        bool OwnsPatches { get; }
        bool SkipCoreHarmony { get; }
        bool AcceptWrites { get; }
        bool OwnsSite(string site);
        Delegate? Get(string site);
        void Set(string site, Delegate? dispatcher);
    }

    internal sealed class FakeSlot : ISlot
    {
        private readonly System.Collections.Generic.Dictionary<string, Delegate?> _slots =
            new(StringComparer.Ordinal);

        public bool OwnsPatches { get; set; } = true;
        public bool SkipCoreHarmony { get; set; }
        public bool AcceptWrites { get; set; } = true;
        /// <summary>Countdown of successful writes; 0 refuses the next TrySet (partial-fill tests).</summary>
        public int RemainingWrites { get; set; } = int.MaxValue;

        /// <summary>
        /// When null, every known site is owned (OwnsPatches). When set, only those
        /// canonical names are owned so a failed Bootstrap patch can be simulated.
        /// </summary>
        public System.Collections.Generic.HashSet<string>? OwnedSites { get; set; }

        public bool OwnsSite(string site)
        {
            if (!OwnsPatches || string.IsNullOrEmpty(site)) return false;
            if (OwnedSites == null) return true;
            string key = CanonicalSite(site);
            return OwnedSites.Contains(site) || OwnedSites.Contains(key);
        }

        public Delegate? Get(string site) =>
            _slots.TryGetValue(site, out var d) ? d : null;

        public void Set(string site, Delegate? dispatcher) => _slots[site] = dispatcher;

        public readonly System.Collections.Generic.Dictionary<string, string> Owners =
            new(StringComparer.Ordinal);

        public bool TryBindOwned(string site, Delegate dispatcher, string? owner)
        {
            if (!OwnsSite(site) || !AcceptWrites) return false;
            if (RemainingWrites <= 0) return false;
            Owners.TryGetValue(site, out var current);
            if (!Progenitor.Bootstrap.HarmonySlotPolicy.MayBindPlugin(current, owner))
                return false;
            RemainingWrites--;
            Set(site, dispatcher);
            if (!string.IsNullOrEmpty(owner))
                Owners[site] = owner;
            return true;
        }

        public bool TryClearOwned(string site, string? owner)
        {
            Owners.TryGetValue(site, out var current);
            if (!Progenitor.Bootstrap.HarmonySlotPolicy.MayClearPlugin(current, owner))
                return false;
            Set(site, null);
            Owners.Remove(site);
            return true;
        }

        public int ClearOwned(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return 0;
            var toClear = new System.Collections.Generic.List<string>();
            foreach (var kv in Owners)
            {
                if (Progenitor.Bootstrap.HarmonySlotPolicy.OwnerMatchesUnload(kv.Value, owner, owner))
                    toClear.Add(kv.Key);
            }
            for (int i = 0; i < toClear.Count; i++)
            {
                Set(toClear[i], null);
                Owners.Remove(toClear[i]);
            }
            return toClear.Count;
        }

        public void ClearAll()
        {
            _slots.Clear();
            Owners.Clear();
        }
    }
#endif

    internal static string CanonicalSite(string? slot)
    {
        if (string.IsNullOrEmpty(slot)) return "";
        switch (slot)
        {
            case SystemBaseUpdatePrefix:
            case SystemBaseUpdatePostfix:
                return SystemBaseUpdate;
            case UnmanagedUpdatePrefix:
            case UnmanagedUpdatePostfix:
                return UnmanagedUpdate;
            case DeserializeLoadFilePrefix:
            case DeserializeLoadFilePostfix:
                return DeserializeLoadFile;
            case StunProcessStartPrefix:
            case StunProcessStartFinalizer:
                return StunProcessStart;
            case ClientBootstrapConnectPrefix:
            case ClientBootstrapConnectPostfix:
                return ClientBootstrapConnect;
            default:
                return slot;
        }
    }

    /// <summary>
    /// Bind a plugin handler into a Bootstrap-owned IL2CPP patch site. Does not
    /// consult <c>Diagnostics.SkipCoreHarmony</c> (that flag only empties Core's
    /// content sites). Returns a lease that nulls the slot on dispose, or null when
    /// Bootstrap did not patch the original. Empty slot = vanilla original.
    /// Owner defaults to the dispatcher's assembly / ALC name.
    /// </summary>
    public static IDisposable? BindPlugin(string site, Delegate dispatcher) =>
        BindPlugin(site, dispatcher, InferOwner(dispatcher));

    /// <summary>
    /// Same as <see cref="BindPlugin(string, Delegate)"/> with an explicit owner.
    /// A second bind by a different owner is refused.
    /// </summary>
    public static IDisposable? BindPlugin(string site, Delegate dispatcher, string? owner)
    {
        if (!TryBindPlugin(site, dispatcher, owner)) return null;
        return new PluginSlotLease(site, owner);
    }

    /// <summary>
    /// Point a plugin / extra-instance slot at <paramref name="dispatcher"/>. Returns
    /// false when Bootstrap has no trampoline for this original (lazy install failed
    /// or the site is unknown). Owner defaults to the dispatcher's assembly / ALC name.
    /// </summary>
    public static bool TryBindPlugin(string site, Delegate dispatcher) =>
        TryBindPlugin(site, dispatcher, InferOwner(dispatcher));

    /// <summary>
    /// Point a plugin slot at <paramref name="dispatcher"/> owned by
    /// <paramref name="owner"/>. A different owner already bound to this site is
    /// refused with a log line naming both.
    /// </summary>
    public static bool TryBindPlugin(string site, Delegate dispatcher, string? owner)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("site is required.", nameof(site));
        if (string.IsNullOrEmpty(owner))
            owner = InferOwner(dispatcher);
        // Core's GcBeforeLaunch holds Bootstrap's one LaunchGame slot; a plugin bind of that
        // site is chained behind Core's dispatcher instead of being refused by owner.
        if (RoutesToLaunchGameChain(site))
            return Progenitor.Networking.GcBeforeLaunch.TryChain(dispatcher, owner);
        return TryBindPluginDirect(site, dispatcher, owner);
    }

    /// <summary>True when <paramref name="site"/> is LaunchGame and Core's dispatcher holds that slot.</summary>
    internal static bool RoutesToLaunchGameChain(string? site) =>
        Progenitor.Networking.GcBeforeLaunch.SlotArmed
        && string.Equals(CanonicalSite(site), LaunchGame, StringComparison.Ordinal);

    /// <summary>Bind straight into Bootstrap's slot (Core's own LaunchGame dispatcher uses this).</summary>
    internal static bool TryBindPluginDirect(string site, Delegate dispatcher, string? owner)
    {
#if PROGENITOR_TESTS
        if (_override != null)
        {
            if (_override is FakeSlot fake)
                return fake.TryBindOwned(site, dispatcher, owner);
            if (!_override.OwnsSite(site) || !_override.AcceptWrites)
                return false;
            _override.Set(site, dispatcher);
            return true;
        }
        return false;
#else
        return BootstrapLink.TryBindPluginSlot(site, dispatcher, owner ?? "");
#endif
    }

    /// <summary>Null one plugin slot. No-op when the site is empty or Bootstrap is absent. Force-clear (no owner check).</summary>
    public static void ClearPlugin(string site) => ClearPlugin(site, owner: null);

    /// <summary>
    /// Null one plugin slot when <paramref name="owner"/> matches the binder.
    /// Empty owner force-clears (ReloaderHost / Uninstall).
    /// </summary>
    public static void ClearPlugin(string site, string? owner)
    {
        if (string.IsNullOrEmpty(site)) return;
        if (RoutesToLaunchGameChain(site))
        {
            Progenitor.Networking.GcBeforeLaunch.ClearChain(owner);
            return;
        }
        ClearPluginDirect(site, owner);
    }

    /// <summary>Null Bootstrap's slot itself (Core's own LaunchGame dispatcher uses this on uninstall).</summary>
    internal static void ClearPluginDirect(string site, string? owner)
    {
        if (string.IsNullOrEmpty(site)) return;
#if PROGENITOR_TESTS
        if (_override is FakeSlot fake)
        {
            fake.TryClearOwned(site, owner);
            return;
        }
        Clear(site);
#else
        BootstrapLink.TryClearPluginSlot(site, owner);
#endif
    }

    /// <summary>Null every plugin slot bound by <paramref name="owner"/>.</summary>
    public static void ClearOwned(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return;
        try { Progenitor.Networking.GcBeforeLaunch.ClearChainOwnedBy(new[] { owner }); }
        catch { /* best-effort; the Bootstrap slots below still clear */ }
#if PROGENITOR_TESTS
        if (_override is FakeSlot fake)
            fake.ClearOwned(owner);
#else
        BootstrapLink.ClearOwnedPluginSlots(owner);
#endif
    }

    /// <summary>
    /// ReloaderHost / fallback PluginReloader: drop every plugin slot whose
    /// dispatcher lives in the unloading assembly or ALC.
    /// </summary>
    public static void ClearPluginContext(Assembly? assembly, string? alcName)
    {
        try
        {
            if (assembly != null)
                Progenitor.Networking.GcBeforeLaunch.ClearChainForAssemblies(new[] { assembly });
            if (!string.IsNullOrEmpty(alcName))
                Progenitor.Networking.GcBeforeLaunch.ClearChainOwnedBy(new[] { alcName });
        }
        catch { /* best-effort; the Bootstrap slots below still clear */ }
#if PROGENITOR_TESTS
        if (_override is FakeSlot fake)
        {
            string? assemblyName = null;
            try { assemblyName = assembly?.GetName().Name; }
            catch { assemblyName = null; }
            if (!string.IsNullOrEmpty(alcName)) fake.ClearOwned(alcName);
            if (!string.IsNullOrEmpty(assemblyName)) fake.ClearOwned(assemblyName);
            return;
        }
#else
        BootstrapLink.ClearPluginSlotsForUnload(assembly, alcName);
#endif
    }

    internal static string InferOwner(Delegate dispatcher)
    {
        if (dispatcher == null) return "";
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

    private sealed class PluginSlotLease : IDisposable
    {
        private string? _site;
        private string? _owner;

        public PluginSlotLease(string site, string? owner)
        {
            _site = site;
            _owner = owner;
        }

        public void Dispose()
        {
            string? site = _site;
            string? owner = _owner;
            _site = null;
            _owner = null;
            if (site != null) ClearPlugin(site, owner);
        }
    }

    /// <summary>
    /// Each slot is its own fill group. Grouping Chat+Connected+Disconnected (or
    /// TriggerSave+BeginCreate) rolled a live trampoline back to null when a later
    /// sibling <see cref="TrySet"/> failed, leaving an owned site unpatched (R14 finding 1).
    /// Prefix/postfix of one original stay independent for the same reason.
    /// Must match <c>HarmonySlotPolicy.FillGroup</c> (the slot name, not the canonical site).
    /// </summary>
    internal static string FillGroup(string? slot) =>
        string.IsNullOrEmpty(slot) ? "" : slot;

    /// <summary>
    /// Core needs a Harmony instance when skip is off and at least one content site is
    /// not owned by Bootstrap. Do not pass null just because some other site is owned.
    /// </summary>
    internal static bool CoreNeedsFallbackHarmony() =>
        CoreNeedsFallbackHarmony(SkipCoreHarmony, HostOwnsSite);

    internal static bool CoreNeedsFallbackHarmony(bool skipCoreHarmony, Func<string, bool>? hostOwnsSite)
    {
        if (skipCoreHarmony) return false;
        if (hostOwnsSite == null) return true;
        for (int i = 0; i < ContentSites.Length; i++)
        {
            if (CoreMayApplyHarmony(hostOwnsSite(ContentSites[i]), skipCoreHarmony))
                return true;
        }
        return false;
    }

    public const string EventsModule = "Events";
    public const string HooksModule = "Hooks";
    public const string CommandsModule = "Commands";
    public const string ContentModule = "Content";
    public const string ConfigModule = "Config";
    public const string PersistenceModule = "Persistence";
    public const string HostedModule = "Hosted";
    public const string LocalizationModule = "Localization";
    public const string NetworkingModule = "Networking";
    public const string ChatModule = "Chat";
    public const string CastlesModule = "Castles";
    public const string SystemsModule = "Systems";
    public const string PlayersModule = "Players";
    public const string EcsModule = "Ecs";
    public const string EffectsModule = "Effects";
    public const string UiModule = "Ui";
    public const string UnitsModule = "Units";

    public static readonly string[] ModuleNames =
    {
        EventsModule,
        HooksModule,
        CommandsModule,
        ContentModule,
        ConfigModule,
        PersistenceModule,
        HostedModule,
        LocalizationModule,
        NetworkingModule,
        ChatModule,
        CastlesModule,
        SystemsModule,
        PlayersModule,
        EcsModule,
        EffectsModule,
        UiModule,
        UnitsModule,
    };

    /// <summary>Names with no process-lifetime Install work. Kept in the canonical list so a typed skip is not an unknown-token warning.</summary>
    internal static readonly string[] NoopModuleNames =
    {
        ChatModule,
        CastlesModule,
        PlayersModule,
    };

    /// <summary>Set when SkipCoreModules is non-empty but a live world exists and the confirm flag is off.</summary>
    internal static bool SkipModulesRefusedLiveWorld { get; set; }

    private static readonly object SkipModulesGate = new();
    private static readonly System.Collections.Generic.HashSet<string> SkipModules =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void SetSkipModules(string? csv, bool refusedLiveWorld)
    {
        SkipModulesRefusedLiveWorld = refusedLiveWorld;
        lock (SkipModulesGate)
        {
            SkipModules.Clear();
            if (refusedLiveWorld) return;
            var parsed = ExpandSkipModules(csv);
            for (int i = 0; i < parsed.Length; i++)
            {
                string? canonical = CanonicalModuleName(parsed[i]);
                if (canonical != null)
                    SkipModules.Add(canonical);
            }
        }
    }

    internal static bool ShouldSkipModule(string module)
    {
        if (string.IsNullOrEmpty(module)) return false;
        if (SkipModulesRefusedLiveWorld) return false;
        lock (SkipModulesGate)
            return SkipModules.Contains(module);
    }

    internal static string? CanonicalModuleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        for (int i = 0; i < ModuleNames.Length; i++)
        {
            if (string.Equals(ModuleNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase))
                return ModuleNames[i];
        }
        return null;
    }

    internal static string[] ParseSkipModules(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        var parts = csv.Split(',');
        int n = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Trim().Length > 0) n++;
        }
        if (n == 0) return Array.Empty<string>();
        var result = new string[n];
        int w = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.Length == 0) continue;
            string name = CanonicalModuleName(token) ?? token;
            bool dup = false;
            for (int j = 0; j < w; j++)
            {
                if (string.Equals(result[j], name, StringComparison.OrdinalIgnoreCase))
                {
                    dup = true;
                    break;
                }
            }
            if (dup) continue;
            result[w++] = name;
        }
        if (w == result.Length) return result;
        var trimmed = new string[w];
        Array.Copy(result, trimmed, w);
        return trimmed;
    }

    internal static string[] UnknownSkipModules(string? csv)
    {
        var parsed = ParseSkipModules(csv);
        int n = 0;
        for (int i = 0; i < parsed.Length; i++)
        {
            if (CanonicalModuleName(parsed[i]) == null) n++;
        }
        if (n == 0) return Array.Empty<string>();
        var unknown = new string[n];
        int w = 0;
        for (int i = 0; i < parsed.Length; i++)
        {
            if (CanonicalModuleName(parsed[i]) == null)
                unknown[w++] = parsed[i];
        }
        return unknown;
    }

    /// <summary>
    /// Modules that only work through the named module. Hooks → Systems is <em>not</em>
    /// a coupling (R16 finding 3): skipping Hooks already leaves SystemHooks.Install
    /// a no-op; Systems still owns virtual-system relays.
    /// </summary>
    internal static string[] ImpliedSkips(string? module)
    {
        string? canonical = CanonicalModuleName(module);
        if (canonical == null) return Array.Empty<string>();
        if (string.Equals(canonical, ContentModule, StringComparison.Ordinal))
            return new[] { NetworkingModule };
        if (string.Equals(canonical, EventsModule, StringComparison.Ordinal))
            return new[] { CommandsModule, ContentModule, NetworkingModule, PersistenceModule };
        return Array.Empty<string>();
    }

    /// <summary>
    /// Message for <see cref="SkippedModuleException"/>. <c>SystemHooks.Add</c> /
    /// <c>ConfigHelper.Bind</c> / <c>ComponentIndex.IndexOf</c> should throw this
    /// when the named module is skipped (TODO: those files are outside the CZ edit set).
    /// </summary>
    internal static string SkippedModuleExceptionMessage(string module) =>
        "Diagnostics.SkipCoreModules: module '" + (module ?? "") + "' is skipped.";

    internal static InvalidOperationException SkippedModuleException(string module) =>
        new InvalidOperationException(SkippedModuleExceptionMessage(module));

    /// <summary>Throw when <paramref name="module"/> is in the skip set.</summary>
    internal static void ThrowIfModuleSkipped(string module)
    {
        if (ShouldSkipModule(module))
            throw SkippedModuleException(module);
    }

    internal static string[] ExpandSkipModules(string? csv)
    {
        var parsed = ParseSkipModules(csv);
        var buf = new string[ModuleNames.Length];
        int n = 0;
        for (int i = 0; i < parsed.Length; i++)
            AddCanonical(parsed[i], buf, ref n);
        if (n == 0) return Array.Empty<string>();
        if (n == buf.Length) return buf;
        var trimmed = new string[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    internal static string[] ImpliedSkipsBeyondRequested(string? module, string[]? requested)
    {
        string? canonical = CanonicalModuleName(module);
        if (canonical == null) return Array.Empty<string>();
        var buf = new string[ModuleNames.Length];
        int n = 0;
        AddImplied(canonical, buf, ref n);
        if (requested != null)
        {
            int w = 0;
            for (int i = 0; i < n; i++)
            {
                if (ContainsSkipName(requested, requested.Length, buf[i])) continue;
                buf[w++] = buf[i];
            }
            n = w;
        }
        if (n == 0) return Array.Empty<string>();
        var trimmed = new string[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    private static void AddCanonical(string? name, string[] buf, ref int n)
    {
        string? canonical = CanonicalModuleName(name);
        if (canonical == null) return;
        if (ContainsSkipName(buf, n, canonical)) return;
        buf[n++] = canonical;
        AddImplied(canonical, buf, ref n);
    }

    private static void AddImplied(string canonical, string[] buf, ref int n)
    {
        var implied = ImpliedSkips(canonical);
        for (int i = 0; i < implied.Length; i++)
            AddCanonical(implied[i], buf, ref n);
    }

    private static bool ContainsSkipName(string[] names, int count, string name)
    {
        int n = count < names.Length ? count : names.Length;
        for (int i = 0; i < n; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static string SkipModulesEnabledLog(string csv) =>
        "Diagnostics.SkipCoreModules=" + csv
        + ": Core will skip installing the named modules. Known names: "
        + string.Join(", ", ModuleNames)
        + ". No-op (no process-lifetime install): "
        + string.Join(", ", NoopModuleNames) + ".";

    internal static string SkipModuleLog(string module) =>
        "Diagnostics.SkipCoreModules: skipped module " + module;

    internal static string SkipCouplingLog(string module, string[] implied)
    {
        string list = implied == null || implied.Length == 0
            ? ""
            : string.Join(", ", implied);
        return "Diagnostics.SkipCoreModules: skipping " + module
            + " also skips " + list
            + " (" + SkipCouplingReason(module) + ")";
    }

    internal static string SkipCouplingReason(string? module)
    {
        string? canonical = CanonicalModuleName(module);
        if (string.Equals(canonical, ContentModule, StringComparison.Ordinal))
            return "client apply gate never opens (Hermit's Shawl)";
        if (string.Equals(canonical, EventsModule, StringComparison.Ordinal))
            return "those modules only work through the event bus";
        return "coupled module";
    }

    internal static string SkipModulesRefusedLiveWorldLog =>
        "Diagnostics.SkipCoreModules refused: a world is live; set Diagnostics.SkipCoreModulesAllowLiveWorld=true to confirm a module-skip dry-run against this save.";

    internal static string UnknownSkipModuleLog(string name) =>
        "Diagnostics.SkipCoreModules: unknown module '" + name + "' (ignored). Known: "
        + string.Join(", ", ModuleNames) + ".";

    internal static string FormatModulePinsSkipped(string module) =>
        "pin-diag module=" + (string.IsNullOrEmpty(module) ? "?" : module) + " skipped";

    internal static bool IsZeroCountHold(string? hold)
    {
        if (string.IsNullOrEmpty(hold)) return true;
        if (hold.StartsWith("before=", StringComparison.Ordinal)
            || hold.StartsWith("fallback=", StringComparison.Ordinal))
            return !HoldHasPositiveInt(hold);
        return false;
    }

    internal static string DropZeroCountPinHolds(string? line)
    {
        if (string.IsNullOrEmpty(line)) return line ?? "";
        if (!TrySplitModulePinLine(line, out var module, out var holds))
            return line;
        int n = 0;
        for (int i = 0; i < holds.Length; i++)
        {
            if (!IsZeroCountHold(holds[i])) n++;
        }
        if (n == holds.Length) return line;
        var kept = new string[n];
        int w = 0;
        for (int i = 0; i < holds.Length; i++)
        {
            if (IsZeroCountHold(holds[i])) continue;
            kept[w++] = holds[i];
        }
        return FormatModulePins(module, kept);
    }

    internal static string ReplaceOrDropHold(string? line, string keyEquals, string? newHold)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(keyEquals))
            return line ?? "";
        if (!TrySplitModulePinLine(line, out var module, out var holds))
            return line;
        int n = 0;
        bool replaced = false;
        var next = new string[holds.Length + 1];
        for (int i = 0; i < holds.Length; i++)
        {
            string hold = holds[i] ?? "";
            if (hold.StartsWith(keyEquals, StringComparison.Ordinal))
            {
                replaced = true;
                if (string.IsNullOrEmpty(newHold)) continue;
                next[n++] = newHold;
                continue;
            }
            next[n++] = hold;
        }
        if (!replaced && !string.IsNullOrEmpty(newHold))
            next[n++] = newHold;
        var kept = new string[n];
        Array.Copy(next, kept, n);
        return FormatModulePins(module, kept);
    }

    private static bool TrySplitModulePinLine(string line, out string module, out string[] holds)
    {
        module = "?";
        holds = Array.Empty<string>();
        const string prefix = "pin-diag module=";
        const string holdsMark = " holds=[";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int mark = line.IndexOf(holdsMark, StringComparison.Ordinal);
        if (mark < 0) return false;
        module = line.Substring(prefix.Length, mark - prefix.Length);
        string inner = line.Substring(mark + holdsMark.Length);
        if (inner.EndsWith("]", StringComparison.Ordinal))
            inner = inner.Substring(0, inner.Length - 1);
        if (inner.Length == 0)
        {
            holds = Array.Empty<string>();
            return true;
        }
        holds = inner.Split(new[] { ", " }, StringSplitOptions.None);
        return true;
    }

    private static bool HoldHasPositiveInt(string hold)
    {
        int i = 0;
        while (i < hold.Length)
        {
            if (hold[i] != '=')
            {
                i++;
                continue;
            }
            i++;
            if (i >= hold.Length || hold[i] < '0' || hold[i] > '9') continue;
            int value = 0;
            while (i < hold.Length && hold[i] >= '0' && hold[i] <= '9')
            {
                value = (value * 10) + (hold[i] - '0');
                i++;
                if (value > 0) return true;
            }
        }
        return false;
    }

    internal static string FormatModulePins(string module, params string[]? holds)
    {
        if (string.IsNullOrEmpty(module)) module = "?";
        if (holds == null || holds.Length == 0)
            return "pin-diag module=" + module + " holds=[]";
        int n = 0;
        for (int i = 0; i < holds.Length; i++)
        {
            if (!string.IsNullOrEmpty(holds[i])) n++;
        }
        if (n == 0)
            return "pin-diag module=" + module + " holds=[]";
        var parts = new string[n];
        int w = 0;
        for (int i = 0; i < holds.Length; i++)
        {
            if (string.IsNullOrEmpty(holds[i])) continue;
            parts[w++] = holds[i];
        }
        return "pin-diag module=" + module + " holds=[" + string.Join(", ", parts) + "]";
    }

    internal static string FormatModulePins(string module, System.Collections.Generic.IReadOnlyList<string>? holds)
    {
        if (holds == null || holds.Count == 0)
            return FormatModulePins(module, Array.Empty<string>());
        var arr = new string[holds.Count];
        for (int i = 0; i < holds.Count; i++)
            arr[i] = holds[i];
        return FormatModulePins(module, arr);
    }

    /// <summary>Per-site bind: fill, fallback-patch, inert, vanilla, or skip.</summary>
    internal enum SiteBind
    {
        Skip,
        Filled,
        Fallback,
        Inert,
        Vanilla,
    }

    /// <summary>
    /// Ask the handoff whether Bootstrap owns this original. Fills the slot when it
    /// does; tells the caller to <c>Harmony.Patch</c> only when it does not. A failed
    /// write on an owned site is inert (no second patch). Does not roll back siblings.
    /// </summary>
    internal static SiteBind Bind(string site, Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("site is required.", nameof(site));
        if (SkipCoreHarmony) return SiteBind.Skip;
        bool owns = HostOwnsSite(site);
        if (owns)
            return TrySet(site, dispatcher) ? SiteBind.Filled : SiteBind.Inert;
        return CoreMayApplyHarmony(owns, SkipCoreHarmony) ? SiteBind.Fallback : SiteBind.Vanilla;
    }

    /// <summary>
    /// Job FLATCH1 (R44 finding 6). True only for the two outcomes that mean a handler is
    /// actually going to run: the slot took the dispatcher, or the caller is cleared to apply its
    /// own Harmony patch. A module's "already hooked" flag may only latch on these.
    ///
    /// <para><see cref="SiteBind.Inert"/> is a <em>failed slot write</em> on a site Bootstrap owns
    /// and <see cref="SiteBind.Vanilla"/> is an unpatched original: latching on either records
    /// "done" for a seam that was never installed, and the module then never retries for the rest
    /// of the generation. <see cref="SiteBind.Skip"/> is the diagnostics opt-out, which is not
    /// permanent either — <see cref="SkipRefusedLiveWorld"/> turns <see cref="SkipCoreHarmony"/>
    /// back off when a live world refuses the skip. <see cref="SiteBind.Fallback"/> only clears
    /// the caller to patch; the caller must still confirm its own patch landed before latching.
    /// </para>
    /// </summary>
    internal static bool BindMayLatch(SiteBind outcome) =>
        outcome == SiteBind.Filled || outcome == SiteBind.Fallback;

    internal static string InertLog(string module, string site) =>
        module + ": Bootstrap owns " + site + " but slot write failed; staying inert for this site (no second patch).";

    internal static string VanillaLog(string module, string site) =>
        module + ": " + site + " is vanilla (Bootstrap did not patch it and Core cannot fallback).";

    internal static bool HostOwnsPatches
    {
        get
        {
#if PROGENITOR_TESTS
            return _override is { OwnsPatches: true };
#else
            return BootstrapLink.OwnsHarmonyPatches;
#endif
        }
    }

    internal static bool SkipCoreHarmony
    {
        get
        {
            if (SkipRefusedLiveWorld) return false;
#if PROGENITOR_TESTS
            return _override is { SkipCoreHarmony: true };
#else
            return BootstrapLink.SkipCoreHarmony;
#endif
        }
    }

    /// <summary>True when Bootstrap patched this original (prefix/postfix map onto the site).</summary>
    internal static bool HostOwnsSite(string site)
    {
#if PROGENITOR_TESTS
        if (_override != null) return _override.OwnsSite(site);
        return false;
#else
        return BootstrapLink.OwnsHarmonySite(site);
#endif
    }

    /// <summary>
    /// Write a typed handler into the host slot. Returns false when skip is on, the host
    /// did not patch this original, or the write is refused. The caller must then either
    /// fall back to <c>Harmony.Patch</c> (only when <see cref="CoreMayApplyHarmony"/> for
    /// this site) or stay inert. Does not roll back siblings; see <see cref="TryFill"/>.
    /// </summary>
    internal static bool TrySet(string site, Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("site is required.", nameof(site));
#if PROGENITOR_TESTS
        if (_override != null)
        {
            if (!_override.OwnsSite(site) || SkipCoreHarmony || !_override.AcceptWrites)
                return false;
            if (_override is FakeSlot limited)
            {
                if (limited.RemainingWrites <= 0) return false;
                limited.RemainingWrites--;
            }
            _override.Set(site, dispatcher);
            return true;
        }
        return false;
#else
        if (!CoreMayFillSlot(HostOwnsSite(site), SkipCoreHarmony)) return false;
        return BootstrapLink.TrySetHarmonySlot(site, dispatcher);
#endif
    }

    private static readonly object FillGate = new();
    private static string? _fillGroup;
    private static readonly System.Collections.Generic.List<string> _fillGroupArmed = new();

    /// <summary>
    /// Fill one slot and, on failure, clear earlier fills in the same
    /// <see cref="FillGroup"/>. Groups are per slot (R14 finding 1), so a later
    /// sibling cannot empty a live trampoline. Prefer <see cref="Bind"/> in modules.
    /// </summary>
    internal static bool TryFill(string site, Delegate dispatcher)
    {
        string group = FillGroup(site);
        lock (FillGate)
        {
            if (!string.Equals(_fillGroup, group, StringComparison.Ordinal))
            {
                _fillGroup = group;
                _fillGroupArmed.Clear();
            }
            if (!TrySet(site, dispatcher))
            {
                for (int i = 0; i < _fillGroupArmed.Count; i++)
                    Clear(_fillGroupArmed[i]);
                _fillGroupArmed.Clear();
                _fillGroup = null;
                return false;
            }
            _fillGroupArmed.Add(site);
            return true;
        }
    }

    /// <summary>
    /// All-or-nothing fill that does not participate in <see cref="TryFill"/> groups.
    /// On any failure the slots already written in this call are cleared.
    /// </summary>
    internal static bool TryFillAll(string[] sites, Delegate[] dispatchers)
    {
        if (sites == null) throw new ArgumentNullException(nameof(sites));
        if (dispatchers == null) throw new ArgumentNullException(nameof(dispatchers));
        if (sites.Length != dispatchers.Length)
            throw new ArgumentException("sites and dispatchers must be the same length.");
        var armed = new System.Collections.Generic.List<string>(sites.Length);
        for (int i = 0; i < sites.Length; i++)
        {
            if (!TrySet(sites[i], dispatchers[i]))
            {
                for (int j = 0; j < armed.Count; j++)
                    Clear(armed[j]);
                return false;
            }
            armed.Add(sites[i]);
        }
        return true;
    }

    internal static void Clear(string site)
    {
        if (string.IsNullOrEmpty(site)) return;
#if PROGENITOR_TESTS
        _override?.Set(site, null);
#else
        BootstrapLink.ClearHarmonySlot(site);
#endif
    }

    internal static void ClearAll()
    {
#if PROGENITOR_TESTS
        if (_override is FakeSlot fake) fake.ClearAll();
        else if (_override != null)
        {
            foreach (var site in AllSites)
                _override.Set(site, null);
            _override.Set(SystemBaseUpdatePrefix, null);
            _override.Set(SystemBaseUpdatePostfix, null);
            _override.Set(UnmanagedUpdatePrefix, null);
            _override.Set(UnmanagedUpdatePostfix, null);
            _override.Set(DeserializeLoadFilePrefix, null);
            _override.Set(DeserializeLoadFilePostfix, null);
            _override.Set(StunProcessStartPrefix, null);
            _override.Set(StunProcessStartFinalizer, null);
            foreach (var pluginSlot in PluginSlotNames)
                _override.Set(pluginSlot, null);
            _override.Set(LateFrame, null);
        }
#else
        BootstrapLink.ClearAllHarmonySlots();
#endif
        ClearLateFrame();
    }

    /// <summary>
    /// Point Bootstrap's late-frame slot at <paramref name="dispatcher"/>. Unlike Harmony
    /// <see cref="TrySet"/>, this does not consult SkipCoreHarmony or site ownership:
    /// FrameTicker is process-lifetime and always owns the pump. Returns false when
    /// Bootstrap is absent (tests without a fake).
    /// </summary>
    internal static bool TrySetLateFrame(Action dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
#if PROGENITOR_TESTS
        if (_override != null)
        {
            if (_override is FakeSlot limited)
            {
                if (!limited.AcceptWrites) return false;
                if (limited.RemainingWrites <= 0) return false;
                limited.RemainingWrites--;
            }
            _override.Set(LateFrame, dispatcher);
            LastLateFrame = dispatcher;
            return true;
        }
        LastLateFrame = dispatcher;
        return false;
#else
        BootstrapLink.SetLateFrame(dispatcher);
        return true;
#endif
    }

    /// <summary>Null the late-frame slot. No-op when Bootstrap is absent.</summary>
    internal static void ClearLateFrame()
    {
#if PROGENITOR_TESTS
        LastLateFrame = null;
        _override?.Set(LateFrame, null);
#else
        BootstrapLink.SetLateFrame(null);
#endif
    }

#if PROGENITOR_TESTS
    /// <summary>Last LateFrame dispatcher written, even when no fake host is installed.</summary>
    internal static Action? LastLateFrame { get; private set; }
#endif
}
