using System;
using System.Globalization;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free rules for Bootstrap-owned Core Harmony patches (step D). Harmony IL2CPP
/// trampolines pin the collectible Core ALC when the patch <c>MethodInfo</c> lives in Core;
/// Bootstrap applies each patch once per process and Core only fills a slot. Tests compile
/// this file without game or Harmony references.
/// </summary>
public static class HarmonySlotPolicy
{
    public const string ConfigSection = "Diagnostics";
    public const string ConfigKey = "SkipCoreHarmony";

    /// <summary>Eleven original methods Core used to patch. Prefix/postfix share one site name.</summary>
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
    /// <summary>
    /// Metadata method name of the live Start body. CanonicalSite maps this onto
    /// <see cref="StunProcessStart"/>; the abstract <c>StunProcess$$Start</c> is not the site.
    /// </summary>
    public const string StunProcessMonoStart = "StunProcess_MONO$$Start";
    public const string SetLanguage = "GameLocalization.SetLanguage";
    public const string BootstrapSystemOnUpdate = "BootstrapSystem.OnUpdate";

    /// <summary>
    /// Save-safety load guard (job FSAVE): prefix on
    /// <c>LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData)</c>.
    /// Server process only, and deliberately
    /// <b>not</b> in <see cref="AllSites"/> / <see cref="ContentSites"/>: it has no Core slot,
    /// Core never fallback-patches it, and <c>Diagnostics.SkipCoreHarmony</c> must not empty it
    /// — a save that already stores a cloned GUID still has to load.
    /// </summary>
    public const string CopyDataFromPrefabs = "LoadPersistenceSystemV2.CopyDataFromPrefabs";

    public const string SystemBaseUpdatePrefix = "SystemBase.Update.prefix";
    public const string SystemBaseUpdatePostfix = "SystemBase.Update.postfix";
    public const string UnmanagedUpdatePrefix = "WorldUnmanagedImpl.UnmanagedUpdate.prefix";
    public const string UnmanagedUpdatePostfix = "WorldUnmanagedImpl.UnmanagedUpdate.postfix";
    public const string DeserializeLoadFilePrefix = "LoadPersistenceSystemV2.DeserializeLoadFile.prefix";
    public const string DeserializeLoadFilePostfix = "LoadPersistenceSystemV2.DeserializeLoadFile.postfix";
    public const string StunProcessStartPrefix = "StunProcess.Start.prefix";
    public const string StunProcessStartFinalizer = "StunProcess.Start.finalizer";

    /// <summary>Dev host_capture: <c>GameHelper.LaunchGame(GameConnect)</c> prefix.</summary>
    public const string LaunchGame = "GameHelper.LaunchGame";
    /// <summary>Dev host_capture: <c>GameBootstrap.Start</c> prefix.</summary>
    public const string GameBootstrapStart = "GameBootstrap.Start";
    /// <summary>Dev host_capture fallback when Start is not patchable.</summary>
    public const string GameBootstrapAwake = "GameBootstrap.Awake";
    /// <summary>Dev chat_observe: <c>ServerBootstrapSystem.SendPacketToUser</c> postfix.</summary>
    public const string SendPacketToUser = "ServerBootstrapSystem.SendPacketToUser";
    /// <summary>Dev extra-instance: skip Steam ticket (identity omitted — ValueType wrapper).</summary>
    public const string GetAuthSessionTicket = "ClientWorldManager.GetAuthSessionTicket";
    /// <summary>Dev extra-instance: rewrite SteamIPv4 → Lidgren.</summary>
    public const string CreateSteamIPv4 = "ConnectAddress.CreateSteamIPv4";
    /// <summary>Dev extra-instance: force <c>ClientConnectData.IsBot</c>.</summary>
    public const string ClientBootstrapConnect = "ClientBootstrapSystem.Connect";
    public const string ClientBootstrapConnectPrefix = "ClientBootstrapSystem.Connect.prefix";
    public const string ClientBootstrapConnectPostfix = "ClientBootstrapSystem.Connect.postfix";
    /// <summary>Dev extra-instance: queued-hail IsBot force.</summary>
    public const string UpdateConnectRequest = "ClientBootstrapSystem.UpdateConnectRequest";
    /// <summary>Dev extra-instance server: skip <c>BeginAuthSession</c> for the fake-id band.</summary>
    public const string Authenticate = "ServerBootstrapSystem.Authenticate";

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

    /// <summary>
    /// The ten content sites SkipCoreHarmony actually empties. Tick is Bootstrap-owned
    /// and is not skipped; logging it as skipped is a lie.
    /// </summary>
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

    /// <summary>
    /// Canonical originals that only a Dev / extra-instance process binds. Bootstrap
    /// patches each lazily on first bind; the trampoline then belongs to Bootstrap
    /// for the process lifetime. Empty slot = vanilla original.
    /// </summary>
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

    /// <summary>Slot names a plugin <c>TryBindPlugin</c>s, including prefix/postfix of one original.</summary>
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

    public const int PluginSiteCount = 9;

    public const string SkipAllowLiveWorldKey = "SkipCoreHarmonyAllowLiveWorld";

    /// <summary>
    /// Core may <c>Harmony.Patch</c> a site only when Bootstrap did not patch that original
    /// and the diagnostic skip is off. Per-site: a failed Bootstrap patch does not block
    /// Core's fallback on the other ten. A host that owns a site but rejected the slot
    /// write stays inert for that site: a second patch on the same original is a double prefix.
    /// </summary>
    public static bool CoreMayApplyHarmony(bool hostOwnsSite, bool skipCoreHarmony) =>
        !hostOwnsSite && !skipCoreHarmony;

    /// <summary>Core may write a slot only when Bootstrap patched that site and skip is off.</summary>
    public static bool CoreMayFillSlot(bool hostOwnsSite, bool skipCoreHarmony) =>
        hostOwnsSite && !skipCoreHarmony;

    /// <summary>
    /// Empty slot, throw, or a missing bool return must run the original. A live prefix
    /// returning false skips it (Hosted refuse-spawn, SystemHooks Skip).
    /// </summary>
    public static bool PrefixRunOriginal(bool slotPresent, bool? slotReturned, bool slotThrew)
    {
        if (!slotPresent || slotThrew || slotReturned == null) return true;
        return slotReturned.Value;
    }

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

    /// <summary>What a tester actually loses when SkipCoreHarmony empties this site.</summary>
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

    public static string FallbackLog =>
        "Bootstrap Harmony slots absent: Core applying Harmony patches itself.";

    public static string FallbackSiteLog(string site) =>
        "Bootstrap Harmony slots absent: Core applying Harmony patch on " + site;

    public static string OwnsPatchesLog(int count) =>
        "HarmonyPatches: installed " + count + " process-lifetime Core patch site(s).";

    public static string OwnsPatchesNoneLog =>
        "HarmonyPatches: installed 0 process-lifetime Core patch sites; OwnsPatches=false so Core may fallback-PatchAll.";

    /// <summary>Operator-facing: this original is unpatched, never a silent empty slot.</summary>
    public static string VanillaSiteLog(string site) =>
        "HarmonyPatches: site " + site + " is vanilla (Bootstrap patch failed); Core may fallback-patch this site only.";

    public static string SlotThrowLog(string site, string message) =>
        "HarmonyPatches: slot " + site + " threw: " + message;

    /// <summary>
    /// Map a slot name (prefix/postfix/finalizer) onto the AllSites original. Unknown
    /// strings pass through so a future site does not look owned by accident.
    /// </summary>
    public static string CanonicalSite(string? slot)
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
            case StunProcessMonoStart:
                return StunProcessStart;
            case ClientBootstrapConnectPrefix:
            case ClientBootstrapConnectPostfix:
                return ClientBootstrapConnect;
            default:
                return slot;
        }
    }

    /// <summary>
    /// Plugin / extra-instance sites are patched on first bind, not at Bootstrap load.
    /// </summary>
    public static bool IsLazySite(string? site)
    {
        if (string.IsNullOrEmpty(site)) return false;
        string key = CanonicalSite(site);
        for (int i = 0; i < PluginSites.Length; i++)
        {
            if (string.Equals(PluginSites[i], key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>True for a Dev/plugin slot name (prefix/postfix map onto <see cref="PluginSites"/>).</summary>
    public static bool IsPluginSlot(string? site) => IsLazySite(site);

    /// <summary>
    /// A plugin may fill a Bootstrap-owned plugin site even when
    /// <c>Diagnostics.SkipCoreHarmony</c> is on — that flag only empties Core's
    /// ten content sites.
    /// </summary>
    public static bool PluginMayFillSlot(bool hostOwnsSite) => hostOwnsSite;

    /// <summary>
    /// A second bind is allowed only by the same owner (or when the slot is empty).
    /// Empty <paramref name="newOwner"/> is always refused (fail closed): an
    /// empty owner is not stored, so accepting it would be last-writer-wins for
    /// the next named bind.
    /// </summary>
    public static bool MayBindPlugin(string? currentOwner, string? newOwner)
    {
        if (string.IsNullOrEmpty(newOwner)) return false;
        if (string.IsNullOrEmpty(currentOwner)) return true;
        return string.Equals(currentOwner, newOwner, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clear is allowed when the slot is empty, the requested owner matches, or
    /// <paramref name="requestedOwner"/> is empty (force: ReloaderHost / Uninstall).
    /// </summary>
    public static bool MayClearPlugin(string? currentOwner, string? requestedOwner)
    {
        if (string.IsNullOrEmpty(currentOwner)) return true;
        if (string.IsNullOrEmpty(requestedOwner)) return true;
        return string.Equals(currentOwner, requestedOwner, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when ReloaderHost should drop this slot for an unloading plugin.
    /// Matches the stored owner against the ALC name and the assembly simple name.
    /// </summary>
    public static bool OwnerMatchesUnload(string? storedOwner, string? alcName, string? assemblyName)
    {
        if (string.IsNullOrEmpty(storedOwner)) return false;
        if (!string.IsNullOrEmpty(alcName)
            && string.Equals(storedOwner, alcName, StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(assemblyName)
            && string.Equals(storedOwner, assemblyName, StringComparison.Ordinal))
            return true;
        return false;
    }

    public static string FormatOwnerRefused(string site, string currentOwner, string newOwner) =>
        "HarmonySlots: plugin slot '" + (site ?? "") + "' is bound by " + (currentOwner ?? "")
        + "; refused bind by " + (newOwner ?? "") + ".";

    public static string FormatEmptyOwnerRefused(string site) =>
        "HarmonySlots: plugin slot '" + (site ?? "") + "' refused bind with empty owner.";

    public static string FormatClearOwnerRefused(string site, string currentOwner, string requestedOwner) =>
        "HarmonySlots: plugin slot '" + (site ?? "") + "' is bound by " + (currentOwner ?? "")
        + "; refused clear by " + (requestedOwner ?? "") + ".";

    public static string FormatClearedOwned(string owner, int count) =>
        "HarmonySlots: cleared " + count.ToString(CultureInfo.InvariantCulture)
        + " plugin slot(s) owned by " + (owner ?? "") + ".";

    /// <summary>
    /// Each slot is its own fill group. Grouping Chat+Connected+Disconnected (or
    /// TriggerSave+BeginCreate) rolled a live trampoline back to null when a later
    /// sibling <c>TrySet</c> failed, leaving an owned site unpatched (R14 finding 1).
    /// Must match <c>HarmonySlotHandoff.FillGroup</c> (the slot name, not the canonical site).
    /// </summary>
    public static string FillGroup(string? slot) =>
        string.IsNullOrEmpty(slot) ? "" : slot;

    /// <summary>
    /// Core needs a Harmony instance when skip is off and at least one content site is
    /// not owned by Bootstrap. Passing null just because <c>HostOwnsPatches</c> is true
    /// (some other site is owned) blocks fallback on the unowned sites (R14 leftover).
    /// Creating Harmony in collectible Core is itself a pin, so this is false when every
    /// content site is owned (or skip is on).
    /// </summary>
    public static bool CoreNeedsFallbackHarmony(bool skipCoreHarmony, Func<string, bool>? hostOwnsSite)
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

    public const string SkipModulesKey = "SkipCoreModules";
    public const string SkipModulesAllowLiveWorldKey = "SkipCoreModulesAllowLiveWorld";
    /// <summary>
    /// Opt-in wide holder scan during a Core swap freeze. Default false: the plain scan
    /// walks Bootstrap statics plus four cheap extra roots. Wide walks allowlisted
    /// assemblies only, skips uninitialized type statics, and stops at 400 ms.
    /// A dry-run whose reason contains <c>dryRunWide</c> also enables the wide scan
    /// for that run only.
    /// </summary>
    public const string HolderScanWideKey = "HolderScanWide";

    /// <summary>
    /// Canonical names for <see cref="SkipModulesKey"/>. A client dry-run bisects this
    /// set until <c>collected=True</c>. Chat / Castles / Players have no process-lifetime
    /// install; skipping them is a documented no-op so the bisect list is complete.
    /// Ecs skip is also a no-op for the type-index cache (the cache still fills).
    /// </summary>
    public static readonly string[] ModuleNames =
    {
        "Events",
        "Hooks",
        "Commands",
        "Content",
        "Config",
        "Persistence",
        "Hosted",
        "Localization",
        "Networking",
        "Chat",
        "Castles",
        "Systems",
        "Players",
        "Ecs",
        "Effects",
        "Ui",
        "Units",
    };

    /// <summary>
    /// Names with no process-lifetime Install work. A bisect walk should not spend a
    /// dry-run on these; they still parse so a typed list is not an unknown-token warning.
    /// </summary>
    public static readonly string[] NoopModuleNames =
    {
        "Chat",
        "Castles",
        "Players",
    };

    /// <summary>
    /// Bound config value plus an optional live reader so a cfg reload is visible on the
    /// next Core.Install without restarting Bootstrap. Tests set <see cref="SkipModulesValue"/>.
    /// </summary>
    public static string SkipModulesValue { get; set; } = "";

    /// <summary>Second confirm for a non-empty skip list against a live world.</summary>
    public static bool SkipModulesAllowLiveWorld { get; set; }

    /// <summary>Optional live read of the BepInEx <c>ConfigEntry</c> (Bootstrap Plugin.Load).</summary>
    public static Func<string>? SkipModulesReader;

    /// <summary>Optional live read of the allow-live-world flag.</summary>
    public static Func<bool>? SkipModulesAllowLiveWorldReader;

    public static string CurrentSkipModules()
    {
        try
        {
            var reader = SkipModulesReader;
            if (reader != null)
                return reader() ?? "";
        }
        catch
        {
            // Fall through to the last bound value.
        }
        return SkipModulesValue ?? "";
    }

    public static bool CurrentSkipModulesAllowLiveWorld()
    {
        try
        {
            var reader = SkipModulesAllowLiveWorldReader;
            if (reader != null)
                return reader();
        }
        catch
        {
            // Fall through to the last bound value.
        }
        return SkipModulesAllowLiveWorld;
    }

    public static string? CanonicalModuleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        for (int i = 0; i < ModuleNames.Length; i++)
        {
            if (string.Equals(ModuleNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase))
                return ModuleNames[i];
        }
        return null;
    }

    /// <summary>Trimmed tokens from a comma-separated skip list. Unknown names are kept as typed.</summary>
    public static string[] ParseSkipModules(string? csv)
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

    public static string[] UnknownSkipModules(string? csv)
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

    public static bool ShouldSkipModule(string module, string? csv, bool refusedLiveWorld)
    {
        if (refusedLiveWorld) return false;
        string? canonical = CanonicalModuleName(module);
        if (canonical == null) return false;
        var parsed = ExpandSkipModules(csv);
        for (int i = 0; i < parsed.Length; i++)
        {
            if (string.Equals(parsed[i], canonical, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Modules that only work through the named module. Skipping Content also skips
    /// Networking so the client apply gate cannot stay closed (Hermit's Shawl). Skipping
    /// Events also skips every module that only subscribes to the event bus.
    /// Hooks → Systems is not a coupling (R16 finding 3).
    /// </summary>
    public static string[] ImpliedSkips(string? module)
    {
        string? canonical = CanonicalModuleName(module);
        if (canonical == null) return Array.Empty<string>();
        if (string.Equals(canonical, "Content", StringComparison.Ordinal))
            return new[] { "Networking" };
        if (string.Equals(canonical, "Events", StringComparison.Ordinal))
            return new[] { "Commands", "Content", "Networking", "Persistence" };
        return Array.Empty<string>();
    }

    /// <summary>
    /// Message for a skipped-module registration refuse. Must match
    /// <c>HarmonySlotHandoff.SkippedModuleExceptionMessage</c>.
    /// </summary>
    public static string SkippedModuleExceptionMessage(string module) =>
        "Diagnostics.SkipCoreModules: module '" + (module ?? "") + "' is skipped.";

    public static InvalidOperationException SkippedModuleException(string module) =>
        new InvalidOperationException(SkippedModuleExceptionMessage(module));

    /// <summary>
    /// Canonical skip list plus implied couplings, unique, requested-first then implied.
    /// Unknown tokens from <see cref="ParseSkipModules"/> are not kept (they cannot skip).
    /// </summary>
    public static string[] ExpandSkipModules(string? csv)
    {
        var parsed = ParseSkipModules(csv);
        var buf = new string[ModuleNames.Length];
        int n = 0;
        AddRange(parsed, buf, ref n);
        if (n == 0) return Array.Empty<string>();
        if (n == buf.Length) return buf;
        var trimmed = new string[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    /// <summary>
    /// Implied names for one requested module that are not themselves in
    /// <paramref name="requested"/>. Transitive (Events → Content → Networking).
    /// </summary>
    public static string[] ImpliedSkipsBeyondRequested(string? module, string[]? requested)
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
                if (ContainsName(requested, buf[i])) continue;
                buf[w++] = buf[i];
            }
            n = w;
        }
        if (n == 0) return Array.Empty<string>();
        var trimmed = new string[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    private static void AddRange(string[] parsed, string[] buf, ref int n)
    {
        for (int i = 0; i < parsed.Length; i++)
            AddCanonical(parsed[i], buf, ref n);
    }

    private static void AddCanonical(string? name, string[] buf, ref int n)
    {
        string? canonical = CanonicalModuleName(name);
        if (canonical == null) return;
        if (ContainsName(buf, n, canonical)) return;
        buf[n++] = canonical;
        AddImplied(canonical, buf, ref n);
    }

    private static void AddImplied(string canonical, string[] buf, ref int n)
    {
        var implied = ImpliedSkips(canonical);
        for (int i = 0; i < implied.Length; i++)
            AddCanonical(implied[i], buf, ref n);
    }

    private static bool ContainsName(string[] names, string name) =>
        ContainsName(names, names.Length, name);

    private static bool ContainsName(string[] names, int count, string name)
    {
        int n = count < names.Length ? count : names.Length;
        for (int i = 0; i < n; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string SkipModulesEnabledLog(string csv) =>
        "Diagnostics.SkipCoreModules=" + csv
        + ": Core will skip installing the named modules. Known names: "
        + string.Join(", ", ModuleNames)
        + ". No-op (no process-lifetime install): "
        + string.Join(", ", NoopModuleNames) + ".";

    public static string SkipModuleLog(string module) =>
        "Diagnostics.SkipCoreModules: skipped module " + module;

    public static string SkipCouplingLog(string module, string[] implied)
    {
        string list = implied == null || implied.Length == 0
            ? ""
            : string.Join(", ", implied);
        return "Diagnostics.SkipCoreModules: skipping " + module
            + " also skips " + list
            + " (" + SkipCouplingReason(module) + ")";
    }

    public static string SkipCouplingReason(string? module)
    {
        string? canonical = CanonicalModuleName(module);
        if (string.Equals(canonical, "Content", StringComparison.Ordinal))
            return "client apply gate never opens (Hermit's Shawl)";
        if (string.Equals(canonical, "Events", StringComparison.Ordinal))
            return "those modules only work through the event bus";
        return "coupled module";
    }

    public static string SkipModulesRefusedLiveWorldLog =>
        "Diagnostics.SkipCoreModules refused: a world is live; set Diagnostics.SkipCoreModulesAllowLiveWorld=true to confirm a module-skip dry-run against this save.";

    public static string UnknownSkipModuleLog(string name) =>
        "Diagnostics.SkipCoreModules: unknown module '" + name + "' (ignored). Known: "
        + string.Join(", ", ModuleNames) + ".";

    /// <summary>Skipped-module pin dump. Distinct from <c>holds=[]</c> (installed, holds nothing).</summary>
    public static string FormatModulePinsSkipped(string module) =>
        "pin-diag module=" + (string.IsNullOrEmpty(module) ? "?" : module) + " skipped";

    /// <summary>
    /// True when a hold is a zero-count summary (Hooks <c>before=0 after=0 …</c> or
    /// Systems <c>fallback=… pluginFrames=0</c> with no positive integer). Those must
    /// not look like a live pin.
    /// </summary>
    public static bool IsZeroCountHold(string? hold)
    {
        if (string.IsNullOrEmpty(hold)) return true;
        if (hold.StartsWith("before=", StringComparison.Ordinal)
            || hold.StartsWith("fallback=", StringComparison.Ordinal))
            return !HoldHasPositiveInt(hold);
        return false;
    }

    /// <summary>
    /// Drop zero-count summaries from a formatted <c>pin-diag module=… holds=[…]</c> line.
    /// </summary>
    public static string DropZeroCountPinHolds(string? line)
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

    /// <summary>
    /// Replace a hold that starts with <paramref name="keyEquals"/> (e.g. <c>clientHandlers=</c>)
    /// or drop it when <paramref name="newHold"/> is null/empty. Used so Networking's pin dump
    /// does not report Content's PrefabRegistry receive handlers.
    /// </summary>
    public static string ReplaceOrDropHold(string? line, string keyEquals, string? newHold)
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

    /// <summary>
    /// Per-module pin dump. Empty holds means the module registered nothing that can outlive
    /// a collectible Core generation (or it was skipped).
    /// </summary>
    public static string FormatModulePins(string module, params string[]? holds)
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

    public static bool IsKnownSlot(string? site)
    {
        if (string.IsNullOrEmpty(site)) return false;
        switch (site)
        {
            case SystemBaseUpdatePrefix:
            case SystemBaseUpdatePostfix:
            case UnmanagedUpdatePrefix:
            case UnmanagedUpdatePostfix:
            case ChatOnUpdate:
            case UserConnected:
            case UserDisconnected:
            case DeserializeLoadFilePrefix:
            case DeserializeLoadFilePostfix:
            case TriggerSave:
            case BeginCreateServerWorld:
            case StunProcessStart:
            case StunProcessStartPrefix:
            case StunProcessStartFinalizer:
            case StunProcessMonoStart:
            case SetLanguage:
            case LaunchGame:
            case GameBootstrapStart:
            case GameBootstrapAwake:
            case SendPacketToUser:
            case GetAuthSessionTicket:
            case CreateSteamIPv4:
            case ClientBootstrapConnect:
            case ClientBootstrapConnectPrefix:
            case ClientBootstrapConnectPostfix:
            case UpdateConnectRequest:
            case Authenticate:
                return true;
            default:
                return false;
        }
    }
}
