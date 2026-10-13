using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ProjectM;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime BepInEx plugin. Owns the pieces that cannot live in a collectible Core
/// (injected <c>FrameTicker</c>, log ring, main-thread queue, plugin ALC host, self-update,
/// native receive detours, clone/tombstone snapshot) and loads Core from
/// <c>BepInEx/progenitor-core/</c> into a collectible ALC. Guid is
/// <c>progenitor</c> so consumer <c>[BepInDependency("progenitor")]</c> is
/// satisfied without a sample change: the chainloader waits for this plugin. Harmony id stays
/// <see cref="HarmonyId"/> so Core <c>UnpatchSelf</c> cannot remove the backup tick patches.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public sealed class Plugin : BasePlugin
{
    /// <summary>
    /// Distinct from Core's Harmony id (<c>progenitor</c>). Must stay this string even
    /// though this plugin's BepInEx guid is now <c>progenitor</c>.
    /// </summary>
    internal const string HarmonyId = "progenitor.bootstrap";

    internal static ManualLogSource? Logger { get; private set; }
    private Harmony? _harmony;

    public override void Load()
    {
        Logger = Log;
        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded in {(IsServerProcess ? "server" : "client")} process.");
        MigrateRenamedConfigFiles();
        // Before anything else: the first scene's Awake resolves Application.isBatchMode.
        HeadlessClient.TryInstall(IsServerProcess, Log.LogInfo);
        try
        {
            // Job MODS2: an uninstall the player asked for on the Configuration tab is honored
            // here — before BindDiagnostics, before any patch and before CoreLoader, because the
            // files being removed are the ones this process would otherwise load. An applied
            // request returns from Load: this process runs without Progenitor.
            if (UninstallAtStart.HonourIfRequested(Paths.BepInExRootPath, Log.LogInfo))
                return;
            // WORLDRESTORE1: a world restore an admin asked for is applied here, in a server process, before any patch and
            // before Core: GameBootstrap.Start has not resolved the save to load yet. Also logs the phase 0 timing line.
            if (IsServerProcess) WorldRestoreAtStart.Run(Paths.BepInExRootPath, Log.LogInfo);
            // AUTOUPDATE5: the start-loop guard for Progenitor's own update. Before any promote (the running image is the
            // file) and before Core is read: a release that failed to start restores the previous copy here.
            BootIdentity.CaptureAndCheck(Paths.BepInExRootPath, Log);
            BindDiagnostics();
            // Job FMAN4: move accepted server packages out of progenitor-pending/ and into the
            // reload folder here — before ProcessHost.Install starts the tick that owns the first
            // ReloaderHost.Scan, and nowhere else in the process. See PendingInstalls.
            PendingInstalls.PromoteAtStart();
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(IsServerProcess ? typeof(ServerTick) : typeof(ClientTick));
            HarmonyPatches.Install(_harmony);
            try { UiSlots.Install(); }
            catch (Exception e) { Log.LogWarning("UiSlots.Install: " + e.Message); }
            try { IconCache.Install(); }
            catch (Exception e) { Log.LogWarning("IconCache.Install: " + e.Message); }
            try { Progenitor.Ui.UiInputContext.Install(this); }
            catch (Exception e) { Log.LogWarning("UiInputContext.Install: " + e.Message); }
            ProcessHost.Install(this);
        }
        catch (Exception e)
        {
            Log.LogError($"Bootstrap install failed; Core will not load: {e}");
        }
    }

    /// <summary>
    /// Unload order: Core first (nulls receive slots while the JMP still points here),
    /// then Harmony, then MarkUninstalled. MarkUninstalled must not Dispose the receive
    /// detour while Core's slot was live — and Bootstrap is process-lifetime, so even
    /// after Core is gone the JMP stays (empty-slot fourcc policy) unless the process
    /// is exiting. See <c>ReceiveDetours.Uninstall</c>.
    /// </summary>
    public override bool Unload()
    {
        try { CoreLoader.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Core Uninstall: {e.Message}"); }
        try { _harmony?.UnpatchSelf(); }
        catch (Exception e) { Logger?.LogWarning($"Bootstrap UnpatchSelf: {e.Message}"); }
        ProcessHost.MarkUninstalled();
        return true;
    }

    /// <summary>
    /// The 2026-10 id rename (<see cref="Progenitor.Plugins.LegacyIds"/>): every Progenitor cfg named with the retired
    /// vendor prefix gets its current name here, before this plugin binds anything and before the chainloader
    /// constructs any plugin that depends on this one, so each of them opens its renamed file with its values. The
    /// old file is kept as <c>.bak</c>. This plugin's own <c>Config</c> was opened on the current name in the
    /// constructor, so it is reloaded once its file exists.
    /// </summary>
    private void MigrateRenamedConfigFiles()
    {
        try
        {
            var lines = Progenitor.Plugins.LegacyIds.MigrateFolder(Paths.ConfigPath, ".cfg");
            foreach (string line in lines) Log.LogInfo(line);
            if (lines.Count > 0 && System.IO.File.Exists(Config.ConfigFilePath)) Config.Reload();
        }
        catch (Exception e)
        {
            Log.LogWarning("Renamed ids: cfg migration failed: " + e.Message);
        }
    }

    /// <summary>
    /// Same rule as Core: decided once from the executable name so dedicated and hosted play
    /// get the same answer.
    /// </summary>
    internal static bool IsServerProcess { get; } =
        Process.GetCurrentProcess().ProcessName.StartsWith("VRisingServer", StringComparison.OrdinalIgnoreCase);

    private void BindDiagnostics()
    {
        ConfigEntry<bool> skip = Config.Bind(
            HarmonySlotPolicy.ConfigSection,
            HarmonySlotPolicy.ConfigKey,
            false,
            "When true, Core installs none of its ten content Harmony patches (Bootstrap slots stay empty; tick stays). Use with a dry-run core_reload to test whether Harmony IL2CPP trampolines pin the collectible Core generation. Do not enable against a save that already stores cloned GUIDs unless SkipCoreHarmonyAllowLiveWorld is also true.");
        ConfigEntry<bool> allowLive = Config.Bind(
            HarmonySlotPolicy.ConfigSection,
            HarmonySlotPolicy.SkipAllowLiveWorldKey,
            false,
            "Required second flag when SkipCoreHarmony is true and a world already exists. Without it Core refuses skip so cloned GUIDs in the live save are not dropped.");
        ProcessHost.SkipCoreHarmony = skip.Value;
        ProcessHost.SkipCoreHarmonyAllowLiveWorld = allowLive.Value;
        if (skip.Value)
            Log.LogWarning(HarmonySlotPolicy.SkipEnabledLog);

        ConfigEntry<string> skipModules = Config.Bind(
            HarmonySlotPolicy.ConfigSection,
            HarmonySlotPolicy.SkipModulesKey,
            "",
            "Comma-separated Core module names to skip at Install so a client dry-run can bisect which module pins the collectible generation. Known names: Events, Hooks, Commands, Content, Config, Persistence, Hosted, Localization, Networking, Chat, Castles, Systems, Players, Ecs, Effects, Ui, Units. Empty (default) installs every module. Refused on a live world unless SkipCoreModulesAllowLiveWorld is also true. First boot / menu (no Worlds.Server or Client_0 yet) is not a live world: skip applies, and that is by design. Content also skips Networking (apply gate); Events also skips Commands, Content, Networking, Persistence.");
        ConfigEntry<bool> skipModulesLive = Config.Bind(
            HarmonySlotPolicy.ConfigSection,
            HarmonySlotPolicy.SkipModulesAllowLiveWorldKey,
            false,
            "Required second flag when SkipCoreModules is non-empty and a world already exists. Without it Core refuses skip so a live save is not left without EventHooks / PrefabRegistry / Handshake. Not consulted on first boot before a world exists.");
        HarmonySlotPolicy.SkipModulesValue = skipModules.Value ?? "";
        HarmonySlotPolicy.SkipModulesAllowLiveWorld = skipModulesLive.Value;
        HarmonySlotPolicy.SkipModulesReader = () => skipModules.Value ?? "";
        HarmonySlotPolicy.SkipModulesAllowLiveWorldReader = () => skipModulesLive.Value;
        if (!string.IsNullOrWhiteSpace(skipModules.Value))
            Log.LogWarning(HarmonySlotPolicy.SkipModulesEnabledLog(skipModules.Value));

        ConfigEntry<bool> holderWide = Config.Bind(
            HarmonySlotPolicy.ConfigSection,
            HarmonySlotPolicy.HolderScanWideKey,
            false,
            "When true, a Core swap freeze walks statics of allowlisted assemblies only (BepInEx.*, 0Harmony, MonoMod.*, Il2CppInterop.Runtime, Progenitor.Bootstrap, BepInEx/plugins) plus Logger / Harmony / known-initialized Il2CppInterop tables. Types with a static constructor are skipped unless already initialized. Hard-capped at 400 ms (pin-diag holder-scan truncated ms=N). Default false. A dry-run core_reload with reason dryRunWide also enables it for that run.");
        ProcessHost.HolderScanWide = holderWide.Value;
        if (holderWide.Value)
            Log.LogWarning("Diagnostics.HolderScanWide=true: Core swap freeze will run the wide holder scan.");

        ConfigEntry<bool> watchCore = Config.Bind(
            CoreWatchPolicy.ConfigSection,
            CoreWatchPolicy.ConfigKey,
            CoreWatchPolicy.DefaultWatchCore,
            "When true, Bootstrap watches BepInEx/progenitor-core/Progenitor.dll.next and, once two scans see the same length and write time, runs the same Core swap as the bridge core_reload op (reason=watch). Default FALSE: this is an automatic code-loading path, so it is opt-in for developers rather than on for everyone (set it to true here, as tools/deploy.ps1 does for our own trees). Ignored on a hosted child and on a client that is hosting a private game (the child keeps the copied Core). Refused while a swap is already in flight; a refused request retries on the next stable scan. An image whose SHA-256 is not in BepInEx/config/progenitor-allow.txt is quarantined and never loaded when that file exists.");
        ProcessHost.WatchCore = watchCore.Value;
        watchCore.SettingChanged += (_, _) =>
        {
            try { ProcessHost.WatchCore = watchCore.Value; }
            catch { /* tearing down */ }
        };
        if (!watchCore.Value)
            Log.LogInfo(CoreWatchPolicy.FormatDisabled());

        // Save-safety load guard (job FSAVE). Bound here because the guard must work in a
        // process where no Core and no plugin ever loads; both entries are plain bools.
        SaveSafety.BindConfig(Config);
    }

    private static class ServerTick
    {
        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUpdate))]
        [HarmonyPostfix]
        private static void Postfix() => ProcessHost.Tick();
    }

    private static class ClientTick
    {
        [HarmonyPatch(typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.OnUpdate))]
        [HarmonyPostfix]
        private static void Postfix() => ProcessHost.Tick();
    }
}
