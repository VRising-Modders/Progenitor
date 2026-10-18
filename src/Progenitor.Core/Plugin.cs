using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;

namespace Progenitor;

/// <summary>
/// Framework facade and Install/Uninstall body. Core is no longer a chainloader plugin: Bootstrap
/// <c>LoadFromStream</c>s this assembly from <c>BepInEx/progenitor-core/</c> into a collectible
/// ALC and calls <see cref="Runtime.Install"/>. Public surface for mods (<see cref="Logger"/>,
/// <see cref="Version"/>, <see cref="IsServerProcess"/>, <see cref="Frame"/>) is unchanged.
/// When Bootstrap is absent (tests, or a Core dumped where a chainloader can still run it) this
/// type still owns the fallback tick patches and log ring; it cannot inject <c>FrameTicker</c>
/// without a <c>BasePlugin</c> instance.
/// </summary>
public sealed class Plugin
{
    internal static ManualLogSource? Logger { get; private set; }
    private static bool _installed;

    /// <summary>Raised once per frame on the main thread, after queued main-thread work has run.</summary>
    public static event Action? Frame;

    public static string Version => MyPluginInfo.PLUGIN_VERSION;
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// Install Core modules. <paramref name="log"/> is Bootstrap's <c>Progenitor</c> log source
    /// when present; otherwise this method creates one so grep still sees source name Progenitor.
    /// Returns false when already installed (a second call is a no-op and must not rehydrate
    /// content memory or touch Bootstrap lifetime slots Hosted just set).
    /// </summary>
    internal static bool Install(ManualLogSource? log)
    {
        if (_installed) return false;

        var logger = log ?? BepInEx.Logging.Logger.CreateLogSource(MyPluginInfo.PLUGIN_NAME);
        Logger = logger;
        bool bootstrap = BootstrapLink.TryAttach();
        if (bootstrap)
            logger.LogInfo("Bootstrap present: Core delegates process-lifetime services.");
        else
        {
            logger.LogInfo(BootstrapLink.AbsentLog);
            Dev.LogBuffer.Install();
        }
        logger.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded in {(IsServerProcess ? "server" : "client")} process.");

        // SETTINGS1 stage 3 (review of 5a6e40f8, finding 1): the Mods switch's "taken with it" lists
        // belong to the process that recorded them; drop another process's before any plugin loads or
        // any server pause asks whether a plugin is held.
        try { Progenitor.Plugins.ModEnableState.PruneOtherSessions(); }
        catch (Exception e) { logger.LogWarning("Mods: on/off state could not be pruned: " + e.Message); }

        // Decided 2026-09-29: likes and ratings are removed; the old local likes file goes once.
        try { Progenitor.Install.LegacyLikesCache.DeleteIfPresent(BepInEx.Paths.BepInExRootPath, line => logger.LogInfo(line)); }
        catch (Exception e) { logger.LogWarning("Mods: likes cache cleanup threw: " + e.Message); }

        // Tick-ownership decision must run even when Bootstrap already owns FrameTicker
        // (the matrix logs StayInert / refuse ClassInjector). Skipping this left the
        // leftover "Core never consulted TickOwnership" hole.
        Ticks.FrameTicker.Install(null);

        HarmonySlotHandoff.SkipRefusedLiveWorld = false;
        if (HarmonySlotHandoff.SkipCoreHarmony && LiveWorldExists() && !BootstrapLink.SkipCoreHarmonyAllowLiveWorld)
        {
            HarmonySlotHandoff.SkipRefusedLiveWorld = true;
            logger.LogError(HarmonySlotHandoff.SkipRefusedLiveWorldLog);
        }

        string skipModulesCsv = BootstrapLink.SkipCoreModules;
        bool skipModulesOn = !string.IsNullOrWhiteSpace(skipModulesCsv);
        HarmonySlotHandoff.SkipModulesRefusedLiveWorld = false;
        if (skipModulesOn && LiveWorldExists() && !BootstrapLink.SkipCoreModulesAllowLiveWorld)
        {
            HarmonySlotHandoff.SkipModulesRefusedLiveWorld = true;
            logger.LogError(HarmonySlotHandoff.SkipModulesRefusedLiveWorldLog);
        }
        HarmonySlotHandoff.SetSkipModules(skipModulesCsv, HarmonySlotHandoff.SkipModulesRefusedLiveWorld);
        if (skipModulesOn && !HarmonySlotHandoff.SkipModulesRefusedLiveWorld)
        {
            logger.LogWarning(HarmonySlotHandoff.SkipModulesEnabledLog(skipModulesCsv));
            foreach (var unknown in HarmonySlotHandoff.UnknownSkipModules(skipModulesCsv))
                logger.LogWarning(HarmonySlotHandoff.UnknownSkipModuleLog(unknown));
            var requested = HarmonySlotHandoff.ParseSkipModules(skipModulesCsv);
            for (int i = 0; i < requested.Length; i++)
            {
                var implied = HarmonySlotHandoff.ImpliedSkipsBeyondRequested(requested[i], requested);
                if (implied.Length == 0) continue;
                logger.LogWarning(HarmonySlotHandoff.SkipCouplingLog(requested[i], implied));
            }
        }

        if (HarmonySlotHandoff.SkipCoreHarmony)
        {
            logger.LogWarning(HarmonySlotHandoff.SkipEnabledLog);
            logger.LogInfo(HarmonySlotHandoff.SkipTickStaysLog);
            foreach (var site in HarmonySlotHandoff.ContentSites)
                logger.LogInfo(HarmonySlotHandoff.SkipVanillaLog(site));
        }
        else if (!HarmonySlotHandoff.HostOwnsPatches)
            logger.LogInfo(HarmonySlotHandoff.FallbackLog);
        if (!bootstrap)
            Reload.HarmonyFallback.PatchTick(IsServerProcess);
        Reload.HarmonyFallback.InstallModules();
        Effects.Assets.Install();
        Effects.ServerBeam.Install();
        Progenitor.Ui.Ui.Install();
        // ctx.Input: the director and its release key (Core's ui cfg, so after Ui). Client only;
        // the hooks are armed by the first lease.
        try { Inputs.InputFrame.Install(); }
        catch (Exception e) { Logger?.LogWarning($"Input.Install: {e.Message}"); }
        Units.Units.Install();
        // Buffs: server-side timed buff application (ctx.Buffs). Install hooks the once-per-frame
        // poll that turns a queued ApplyBuff into a confirmed application and notices expiry;
        // Uninstall finishes every live application so none outlives this Core generation.
        Buffs.Buffs.Install();
        // ctx.Units.SetLook: the server records looks and sends them on 'PruL'; the client applies
        // them to unit models from a Plugin.Frame pass. Released (and client models restored) below.
        Units.UnitLooks.Install();
        // Worn item meshes: a client frame pass that skins DefineItem().Wearable sketches
        // onto the wearer and hides the donor renderers. Server registrations still write
        // model prefab fields from ApplyOne; this pass is the client half.
        Content.WearableAppearance.Install();
        Content.GuideOutfit.Install();
        // Castles: read-only, but Install hooks the once-per-second heart change detector that
        // backs CastleHeartPlaced / CastleHeartDestroyed. CastleReader.Install (the raw reader's
        // skip log) already ran from HarmonyFallback.InstallModules.
        Castles.Castles.Install();
        // ctx.Inventory (engine, watches, container-id book) and the PlayerReady /
        // PlayerTerritoryChanged publishers. Server only; each holds one Plugin.Frame handler.
        try { Inventory.InventoryRuntime.Install(); }
        catch (Exception e) { Logger?.LogWarning($"Inventory.Install: {e.Message}"); }
        try { Castles.CastlePresenceRuntime.Install(); }
        catch (Exception e) { Logger?.LogWarning($"CastlePresence.Install: {e.Message}"); }
        // InventorySortUsed: client only; a Before hook on the client's event sender.
        try { Inventory.InventorySortRuntime.Install(); }
        catch (Exception e) { Logger?.LogWarning($"InventorySort.Install: {e.Message}"); }
        // Container preview (ctx.Inventory.Preview): the Core-owned 'PrCp' channel. Server: authorization and the
        // shared streaming lease; client: the container window, with its hooks only while a preview owns it.
        try { Inventory.ContainerPreviewServer.Install(); Inventory.ContainerPreviewClient.Install(); }
        catch (Exception e) { Logger?.LogWarning($"ContainerPreview.Install: {e.Message}"); }
        // ctx.Servants: hunt returns, sends and the experimental throne list hook. Server only;
        // SystemHooks on three servant systems and one Plugin.Frame handler.
        try { Servants.ServantRuntime.Install(); }
        catch (Exception e) { Logger?.LogWarning($"Servants.Install: {e.Message}"); }
        // ctx.MaterialSource (P3 spike): the provider registry and, when Bootstrap armed them, the
        // availability / payment seam handlers. Both sides (the client binds the display seam).
        try { Materials.MaterialRuntime.Install(); }
        catch (Exception e) { Logger?.LogWarning($"Materials.Install: {e.Message}"); }
        // Death + damage EventBus publishers (UnitDied / PlayerDied / DamageDealt).
        // Kill postfix via PatchRegistry (ctx.Patches path); damage via SystemHooks.After StatChangeSystem.
        Combat.CombatHooks.Install();
        // Job FEPOCH: the world-load epoch. A postfix on LoadPersistenceSystemV2.CopyDataFromPrefabs
        // — the single-shot step of the load — so anything holding a raw entity id
        // (Castles handles, god-mode guards, plugin state) can tell that the save was reloaded.
        // Must come after Castles.Install: handles built during it stamp the current epoch.
        WorldState.WorldLoadHook.Install();
        Content.ItemRemovalRuntime.Install();
        Content.ItemManagementRuntime.Install();
        Content.DataPackRuntime.Install();
        // ctx.Time: after every load (and on a Core install into a running world), resolve a leftover
        // DayNightCycleDisabled ownership marker from a world saved during an override. Server only.
        Clock.TimeOfDayRuntime.Install();
        // ctx.Time.Freeze / SlowMotion: same marker recovery for the game's TimeScale world speed. Server only.
        Clock.TimeScaleRuntime.Install();
        // After every load, point units whose saved tree reference came back null (a private
        // tree-variant entity is not saved) at their binding's prefab tree. Server only.
        Ai.TreeRepair.Install();
        // After every load (and on a Core install into a running world), give damaging hits whose
        // hit condition has no team check a NOT IsAlly(Target) guard. Server only; [Ai] AllySafeHits.
        Ai.AllySafety.Install();
        // The inert buff every API-spawned unit wears; ally-safe hits apply only to its wearers.
        Ai.ApiSpawnMarker.Install();
        // ctx.Units.SetAura: Core-owned inert clones of vanilla aura buffs (elite ring), stripped on
        // the server prefab and re-checked after every world load. Server only.
        Units.UnitAuras.Install();
        // ctx.Units.SetHealthBarHidden: a Core-owned inert clone carrying HideTargetHUD. Server only.
        Units.UnitHealthBars.Install();
        // Both sides: the server announces the inert clones it strips ('PrIc'); the client strips its own
        // copy of each (markers, hide-HUD) the frame the registry builds it.
        Ai.InertClones.Install();
        Networking.Handshake.Install();
        Networking.ServerSettings.Install();
        // Job SERVERINST4: the graceful Stop server channel (reserved id 'PrsQ'). Server side
        // registers the OnReceiveFromClient handler and the save/quit seams; client side registers
        // the reply handler and the request/poll seams. Context-owned on both sides: Uninstall drops
        // the handler and nulls every seam, so no generation's delegate outlives it.
        Progenitor.Install.StopServerEvents.Install();
        // Join-hang mitigation (2026-10-04): a full .NET collection inside every client
        // GameHelper.LaunchGame, through Bootstrap's LaunchGame plugin slot. Client only;
        // [Diagnostics] CollectBeforeWorldLoad. Before any plugin loads, so Core holds the slot
        // and Dev's host_capture chains behind it.
        try { Networking.GcBeforeLaunchLive.Install(); }
        catch (Exception e) { Logger?.LogWarning($"GcBeforeLaunch.Install: {e.Message}"); }
        // AUTOUPDATE1: the pre-join prompt (a slot-routed LaunchGame prefix). Client only.
        try { Networking.PreJoinGate.Install(); }
        catch (Exception e) { Logger?.LogWarning($"PreJoinGate.Install: {e.Message}"); }
        // Job REMOTEINST1: the admin remote install channel (reserved id 'PrRi'). Server side binds
        // [RemoteInstall], builds the install core and registers the request handler; client side
        // registers the reply handler and the Install tab's sender. Context-owned like Stop server.
        Progenitor.Install.RemoteInstallEvents.Install();
        // AUTOUPDATE3: the server's own mod updates ([AutoUpdate], the catalog it shares the remote installer's transport with,
        // the version store, Restart when empty). Server only; after the remote installer, whose transport and settings it uses.
        try { Progenitor.Install.ServerAutoUpdateLive.Install(); }
        catch (Exception e) { Logger?.LogWarning($"ServerAutoUpdate.Install: {e.Message}"); }
        // Photo mode (reserved id 'PrpP'): the client's fly camera, HUD hide and admin-only world
        // pause, off by default ([PhotoMode] Enabled in Core's ui cfg, so after Ui and the input
        // director); the server half answers pause requests. Both sides.
        try { Cameras.PhotoMode.Install(); }
        catch (Exception e) { Logger?.LogWarning($"PhotoMode.Install: {e.Message}"); }
        // Core admin debug tools: the debug buff (.debug), its map powers and the map teleport key.
        // After PhotoMode for the same reason (Core ui cfg, input director); the server half needs
        // ModSave, the prefab registry and the command registry, all installed above.
        try { Debugging.DebugTools.Install(); }
        catch (Exception e) { Logger?.LogWarning($"DebugTools.Install: {e.Message}"); }
        try { Progenitor.Ui.BuildMenuSearch.Install(); }
        catch (Exception e) { Logger?.LogWarning($"BuildMenuSearch.Install: {e.Message}"); }
        // SUPPORT1: Get help's [Support] OutputFolder (Core's ui cfg, client only); the page row reads it.
        try { Progenitor.Support.SupportLive.Install(); }
        catch (Exception e) { Logger?.LogWarning($"SupportLive.Install: {e.Message}"); }
        // SUPPORT1: an admin's Collect logs fetches the server's own bundle over 'PrSL' (both sides).
        try { Progenitor.Support.ServerLogsLive.Install(); }
        catch (Exception e) { Logger?.LogWarning($"ServerLogsLive.Install: {e.Message}"); }
        // DYEALL in Core (2026-10-08): hold a color swatch in Character > Equipment to dye every worn piece.
        // Client only; [DyeAll] in Core's ui cfg, after Ui (the pointer watches and the hold ring).
        try { Dye.DyeAllFeature.Install(); }
        catch (Exception e) { Logger?.LogWarning($"DyeAllFeature.Install: {e.Message}"); }
        // Shift-click the full map to put that spot's [loc:x,z] in chat (client only; a map-scope key in Core's ui cfg).
        try { Ui.MapShare.Install(); }
        catch (Exception e) { Logger?.LogWarning($"MapShare.Install: {e.Message}"); }
        try { Ui.CharacterSheetFeature.Install(); }
        catch (Exception e) { Logger?.LogWarning($"CharacterSheetFeature.Install: {e.Message}"); }
        // CHAT1: the game's chat box stays up longer and a click on it no longer attacks. Client only;
        // [Chat] in Core's ui cfg, after Ui (the input mask it shares with the mod panels).
        try { Progenitor.Chat.ChatWindow.Install(); }
        catch (Exception e) { Logger?.LogWarning($"ChatWindow.Install: {e.Message}"); }
        // Hot-load chat lines (docs/hot-reload.md): after ChatWindow, whose [Chat] HotLoadMessages the client reads.
        try { Reload.HotLoadNoticeLive.Install(); }
        catch (Exception e) { Logger?.LogWarning($"HotLoadNotice.Install: {e.Message}"); }
        // Chat command hints (reserved id 'PrCh'): the server answers a player's catalog request with the
        // commands that player can run; the client asks after the server's hello. One side each.
        try
        {
            Progenitor.Chat.ChatCommandHintsServer.Install();
            Progenitor.Chat.ChatCommandHints.Install();
        }
        catch (Exception e) { Logger?.LogWarning($"ChatCommandHints.Install: {e.Message}"); }
        // Mod compatibility C1/C2 (reserved ids 'PrFt' and 'PrMs'): feature tables and versioned mod messages.
        // Registered through the owned-id path like every Core channel. One side each.
        try { Networking.ModCompatNet.Install(); }
        catch (Exception e) { Logger?.LogWarning($"ModCompatNet.Install: {e.Message}"); }
        try { Networking.ExchangeNet.Install(); }
        catch (Exception e) { Logger?.LogWarning($"ExchangeNet.Install: {e.Message}"); }
        // Job FCOMPAT3: [Compat] RewriteMode is bound like every other Core section, through
        // ConfigHelper on the shared ConfigFile, so the key and its description appear in
        // BepInEx/config/progenitor.cfg instead of being an undocumented string an
        // operator has to know to type. BindEnum stores it as a string: a ConfigEntry<RewriteMode>
        // on the process-lifetime file would pin this collectible generation (evidence 65).
        //
        // Read ONCE, here, and latched for the generation (job FCOMPAT4, R34 finding 12: Adopt
        // installs a reader that answers the latched value, because the load path only consults
        // CompatRewritePolicy.Mode when a reader is set — clearing it, as the first cut did, took
        // the bound value off the load path entirely and sent every plugin load back to the .cfg).
        // Bootstrap's mirror of
        // CompatRewriteHost reads the same [Compat]/RewriteMode entry out of the same .cfg file
        // by hand rather than through Config.Bind, and has to: its reloader answers this question
        // from inside the load path, on a thread where no Core generation and therefore no Core
        // ConfigFile binding exists yet, and Bootstrap cannot compile-reference Core to reach
        // this one. Both sides read the same section, the same key and the same default, and
        // CompatRewriterTests pins the reader against a written .cfg.
        //
        // The file comes from SharedConfig: at process start the chainloader lookup this used to
        // make found nothing (Core installs inside Bootstrap's Load), so the section was skipped
        // until a hot reload. With a Bootstrap that predates the handover it binds, and latches,
        // once the file resolves; Bootstrap's hand reader answers the load path until then.
        try
        {
            Progenitor.Config.SharedConfig.BindWhenAvailable(
                "[" + Compat.CompatRewritePolicy.ConfigSection + "] " + Compat.CompatRewritePolicy.ConfigKey,
                compatConfig =>
                {
                    var rewriteMode = Progenitor.Config.ConfigHelper.BindEnum(
                        compatConfig,
                        Compat.CompatRewritePolicy.ConfigSection,
                        Compat.CompatRewritePolicy.ConfigKey,
                        Compat.CompatRewritePolicy.DefaultMode,
                        Compat.CompatRewritePolicy.ConfigDescription);
                    var mode = Compat.CompatRewritePolicy.Adopt(rewriteMode?.Value);
                    Logger?.LogInfo("Compat: RewriteMode=" + Compat.CompatRewritePolicy.Text(mode)
                                    + " (bound from [" + Compat.CompatRewritePolicy.ConfigSection + "] "
                                    + Compat.CompatRewritePolicy.ConfigKey + ").");
                });
            // The resilience report's rewriter join. Must be installed before Resilience.Install
            // below, which is what runs the pass that formats the block.
            Compat.CompatResilienceJoin.Install();
        }
        catch (Exception e)
        {
            logger.LogWarning("Compat: binding [Compat] RewriteMode threw ("
                              + e.Message + "); using " + Compat.CompatRewritePolicy.Text(Compat.CompatRewritePolicy.DefaultMode) + ".");
        }
        // M4.1 update resilience: one background pass that fingerprints the game build and
        // reports what a game update moved. Never blocks the load and never refuses one.
        Resilience.Resilience.LogSink = line => logger.LogInfo(line);
        Resilience.Resilience.Install();
        logger.LogInfo($"Reload folder: {Reload.PluginReloader.Folder}");
        Reload.SelfUpdate.Install();
        // AUTOUPDATE5: Progenitor's own update (detection on the cached index, the apply, the swap confirmation, the health mark).
        try { Progenitor.Install.SelfUpdatesLive.Install(); }
        catch (System.Exception e) { logger.LogWarning("SelfUpdates.Install: " + e.Message); }
        if (bootstrap)
        {
            // Hosted owns LifetimeQuitting / LifetimeFrame for this generation. Do not
            // null them here: Hosted.Install just pointed them at the child-completion
            // callbacks, and a restore while a child is running would drop those slots.
            BootstrapLink.NotifyCoreReady();
        }
        _installed = true;
        return true;
    }

    /// <summary>
    /// Reverse <see cref="Install"/>. Nulls receive slots before dropping handlers so a packet
    /// during teardown cannot land in a half-cleared dictionary or, when Bootstrap owns the JMP,
    /// be fed to the game as an unknown event id. Bootstrap-owned <c>INativeDetour</c>s are not
    /// Disposed here.
    /// </summary>
    internal static bool Uninstall()
    {
        if (!_installed && Reload.HarmonyFallback.Instance == null) return true;

        if (HostedStartIsInFlight())
        {
            Logger?.LogWarning(
                "Core Uninstall refused: Hosted StunProcess.Start is in flight; UnpatchSelf would run on the Start stack. Retry the Core swap after the child spawn finishes.");
            return false;
        }

        // Shared ConfigFile is process-lifetime (Bootstrap Plugin). Drop Core's entries
        // and SettingChanged handlers before module uninstall so a Core-typed ConfigEntry
        // cannot outlive this generation (evidence 65). Idempotent.
        // Queued shared-config binds hold Core delegates; drop them first.
        Progenitor.Config.SharedConfig.Uninstall();
        Progenitor.Config.ConfigHelper.UninstallAll();
        // Drop the resilience log sink first: a pass still running must not log this
        // generation's block after the ALC starts unloading.
        try { Resilience.Resilience.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Resilience.Uninstall: {e.Message}"); }
        // FACC: drop the generated access layer's resolved pointers (default-ALC objects only; pins nothing, but a new generation must re-resolve).
        try { Progenitor.Access.Access.Reset(); }
        catch (Exception e) { Logger?.LogWarning($"Access.Reset: {e.Message}"); }
        // Ui before Effects: Unwire EventTrigger/Button UnityEvents, then
        // DestroyByPrefix, before any leftover effects GO is torn down. The
        // cascade yields a FrameTicker tick after this Uninstall before ALC.Unload.
        try { Progenitor.Install.SelfUpdatesLive.Uninstall(); }
        catch (System.Exception e) { Logger?.LogWarning($"SelfUpdates.Uninstall: {e.Message}"); }
        try { Progenitor.Install.StopServerEvents.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"StopServerEvents.Uninstall: {e.Message}"); }
        try { Progenitor.Install.ServerAutoUpdateLive.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ServerAutoUpdate.Uninstall: {e.Message}"); }
        try { Progenitor.Install.RemoteInstallEvents.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"RemoteInstallEvents.Uninstall: {e.Message}"); }
        try { Progenitor.Support.ServerLogsLive.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ServerLogsLive.Uninstall: {e.Message}"); }
        try { Networking.GcBeforeLaunchLive.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"GcBeforeLaunch.Uninstall: {e.Message}"); }
        try { Networking.PreJoinGate.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"PreJoinGate.Uninstall: {e.Message}"); }
        try { Progenitor.Install.ClientUpdates.Reset(); }
        catch (Exception e) { Logger?.LogWarning($"ClientUpdates.Reset: {e.Message}"); }
        try { Progenitor.Install.ClientAutoUpdate.Reset(); }
        catch (Exception e) { Logger?.LogWarning($"ClientAutoUpdate.Reset: {e.Message}"); }
        Networking.ServerSettings.Uninstall();
        // DYEALL: before Ui, so its swatch watches, ring and palette line go while Ui is still installed.
        try { Dye.DyeAllFeature.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"DyeAllFeature.Uninstall: {e.Message}"); }
        try { Ui.CharacterSheetFeature.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"CharacterSheetFeature.Uninstall: {e.Message}"); }
        // CHAT1: before Ui, so its click guard leaves the game's input list while the input mask it
        // shares is still installed; Ui's teardown then forgets the pointer either way.
        // Hot-load chat lines: leaves its plain-text state for the next Core generation (AppDomain data).
        try { Reload.HotLoadNoticeLive.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"HotLoadNotice.Uninstall: {e.Message}"); }
        try { Progenitor.Chat.ChatWindow.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ChatWindow.Uninstall: {e.Message}"); }
        // Chat link handlers: plugin contexts dispose their own; this drops any left so no plugin delegate outlives Core.
        try { Progenitor.Chat.ChatLinkRegistry.Clear(); }
        catch (Exception e) { Logger?.LogWarning($"ChatLinkRegistry.Clear: {e.Message}"); }
        try
        {
            Progenitor.Chat.ChatCommandHints.Uninstall();
            Progenitor.Chat.ChatCommandHintsServer.Uninstall();
        }
        catch (Exception e) { Logger?.LogWarning($"ChatCommandHints.Uninstall: {e.Message}"); }
        try { Networking.ExchangeNet.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ExchangeNet.Uninstall: {e.Message}"); }
        try { Networking.ModCompatNet.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ModCompatNet.Uninstall: {e.Message}"); }
        try { Progenitor.Ui.Ui.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Ui.Uninstall: {e.Message}"); }
        try { Effects.Assets.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Effects.Uninstall: {e.Message}"); }
        try { Effects.ServerBeam.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Effects.ServerBeam.Uninstall: {e.Message}"); }
        try { Ai.TreeRepair.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"TreeRepair.Uninstall: {e.Message}"); }
        // Points every guarded hit slot back at its original condition (copies are never freed).
        try { Ai.AllySafety.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"AllySafety.Uninstall: {e.Message}"); }
        try { Ai.ApiSpawnMarker.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ApiSpawnMarker.Uninstall: {e.Message}"); }
        // The build-menu search first: it holds an exclusive input hold while its box has focus.
        try { Progenitor.Ui.BuildMenuSearch.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"BuildMenuSearch.Uninstall: {e.Message}"); }
        // Photo mode next: it holds a camera shot, an input lease, hidden HUD canvases and (on the
        // server) world-speed freezes, all of which the modules below would otherwise tear out from under it.
        try { Cameras.PhotoMode.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"PhotoMode.Uninstall: {e.Message}"); }
        // The debug tools before the map lease sweep and every world-facing module: their unload
        // clears the Immortal and kill-path guard the debug buff granted while GodMode, ModSave and
        // the world epoch are all still installed.
        try { Ui.MapShare.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"MapShare.Uninstall: {e.Message}"); }
        try { Ui.MapFocus.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"MapFocus.Uninstall: {e.Message}"); }
        try { Debugging.DebugTools.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"DebugTools.Uninstall: {e.Message}"); }
        // MAPTP1: a reveal lease a plugin did not release still puts the map's reveal flag back.
        try { Ui.WorldMap.ReleaseAll(); }
        catch (Exception e) { Logger?.LogWarning($"WorldMap.ReleaseAll: {e.Message}"); }
        // Before the world-facing modules: the camera owner puts the top-down camera back so a Core
        // swap never leaves the player in a scripted free or orbit shot.
        try { Cameras.CameraFrame.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Camera.Uninstall: {e.Message}"); }
        // Model copies are plain GameObjects under progenitor-models-*; the held sections' requests
        // this generation added are removed so a Core swap does not leave them loaded forever.
        try { Film.FilmFrame.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Film.Uninstall: {e.Message}"); }
        try { Models.ModelFrame.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Models.Uninstall: {e.Message}"); }
        // Every input lease ends (CoreUnloaded) and the input hooks go, so a swap never leaves a
        // mod steering the player from an unloaded generation.
        try { Inputs.InputFrame.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Input.Uninstall: {e.Message}"); }
        try { Units.UnitAuras.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"UnitAuras.Uninstall: {e.Message}"); }
        try { Units.UnitHealthBars.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"UnitHealthBars.Uninstall: {e.Message}"); }
        try { Ai.InertClones.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"InertClones.Uninstall: {e.Message}"); }
        try { Units.UnitLooks.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"UnitLooks.Uninstall: {e.Message}"); }
        try { Content.GuideOutfit.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"GuideOutfit.Uninstall: {e.Message}"); }
        try { Content.WearableAppearance.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"WearableAppearance.Uninstall: {e.Message}"); }
        // Buffs before Units, mirroring the Units-then-Buffs install order. A buff can be worn by a
        // Core-spawned unit, so Units teardown can destroy the wearer first; Buffs.Uninstall would
        // then find nothing to remove and log a false residual. Both run before Castles and the
        // world-epoch teardown: a live application holds a plugin owner reference and a
        // Plugin.Frame handler, and its epoch guard needs WorldEpoch while it is evaluated.
        try { Buffs.Buffs.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Buffs.Uninstall: {e.Message}"); }
        try { Units.Units.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Units.Uninstall: {e.Message}"); }
        // Castles holds one Plugin.Frame handler and the change detector's previous snapshot;
        // both must go before the ALC unloads or they pin this Core generation.
        try { Servants.ServantRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Servants.Uninstall: {e.Message}"); }
        // Materials first: its seam handlers read inventories and castles.
        try { Materials.MaterialRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Materials.Uninstall: {e.Message}"); }
        try { Castles.CastlePresenceRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"CastlePresence.Uninstall: {e.Message}"); }
        try { Inventory.InventoryRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Inventory.Uninstall: {e.Message}"); }
        try { Inventory.InventorySortRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"InventorySort.Uninstall: {e.Message}"); }
        try { Inventory.ContainerPreviewClient.Uninstall(); Inventory.ContainerPreviewServer.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"ContainerPreview.Uninstall: {e.Message}"); }
        // Any shared streaming-origin lease still running (plugins end their own on unload): restore the users' origins.
        try { Cameras.SharedObservation.EndAll(); }
        catch (Exception e) { Logger?.LogWarning($"SharedObservation.EndAll: {e.Message}"); }
        try { Castles.Castles.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Castles.Uninstall: {e.Message}"); }
        // Abilities: the no-cooldowns Plugin.Frame handler and its hold table.
        try { Abilities.NoCooldownsRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"NoCooldowns.Uninstall: {e.Message}"); }
        // Time of day: the override tick; puts DayNightCycleDisabled back if an override was live.
        try { Clock.TimeOfDayRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"TimeOfDay.Uninstall: {e.Message}"); }
        // World speed: the lease tick; puts TimeScale.Value back if a Freeze / SlowMotion was live.
        try { Clock.TimeScaleRuntime.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"TimeScale.Uninstall: {e.Message}"); }
        // Job FHARM: every Harmony patch this generation applied — Core's god-mode kill-path
        // prefixes and anything a plugin registered through ctx.Patches — has its patch method
        try { Players.PlayerControl.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Players.Uninstall: {e.Message}"); }
        try { Combat.CombatHooks.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Combat.Uninstall: {e.Message}"); }
        // Job FHARM: every Harmony patch this generation applied — Core's god-mode kill-path
        // prefixes and anything a plugin registered through ctx.Patches — has its patch method
        // in a collectible assembly, so an applied patch pins that ALC and the unload silently
        // does nothing. Plugins were swept first (PluginReloader.SweepRegistrations); this
        // takes the rest, and clears the guarded-entity set that would otherwise hold plugin
        // owner references. Installed lazily, so this is a no-op when nobody patched anything.
        try { WorldState.WorldLoadHook.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"WorldLoadHook.Uninstall: {e.Message}"); }
        try { Patches.Patches.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"Patches.Uninstall: {e.Message}"); }
        // Core-owned FindEntry JMP: drop it before NativePinRegistry releases this
        // generation, or the next vanilla call jumps into freed memory (R22 #3).
        try { Native.KeyTableProbe.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"KeyTableProbe.Uninstall: {e.Message}"); }
        Networking.CustomEvents.UninstallClientReceive();
        Networking.CustomEvents.UninstallServerReceive();
        Networking.Handshake.Uninstall();
        Networking.ModManifest.Uninstall();
        Hosted.HostedServerLauncher.Uninstall();
        Persistence.ModSave.Uninstall();
        Content.DataPackRuntime.Uninstall();
        Content.ItemManagementRuntime.Uninstall();
        Content.ItemRemovalRuntime.Uninstall();
        Content.PrefabRegistry.Uninstall();
        Commands.CommandSystem.Uninstall();
        Reload.HarmonyFallback.UninstallSystemHooks();
        Events.EventHooks.Uninstall();
        try { HarmonySlotHandoff.ClearAll(); }
        catch (Exception e) { Logger?.LogWarning($"HarmonySlotHandoff.ClearAll: {e.Message}"); }
        HarmonySlotHandoff.SkipRefusedLiveWorld = false;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        Reload.HarmonyFallback.UnpatchCore();
        try { Reload.SelfUpdate.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"SelfUpdate.Uninstall: {e.Message}"); }
        try { Dev.LogBuffer.Uninstall(); }
        catch (Exception e) { Logger?.LogWarning($"LogBuffer.Uninstall: {e.Message}"); }
        // After every plugin sweep and every module: System.Text.Json's process-wide default
        // options cache each type they serialized, and a cached Core or plugin type pins this
        // generation for the life of the process (live dump, 2026-09-25).
        try
        {
            int dropped = Reload.JsonCacheSweep.ReleaseProcessCaches(out var unsupported);
            if (unsupported != null)
                Logger?.LogWarning($"JsonCacheSweep: this runtime has no {unsupported}; a cached collectible type may pin this generation.");
            else
                Logger?.LogInfo($"JsonCacheSweep: dropped {dropped} cache entr{(dropped == 1 ? "y" : "ies")} for collectible types.");
        }
        catch (Exception e) { Logger?.LogWarning($"JsonCacheSweep: {e.Message}"); }
        _installed = false;
        Frame = null;
        // A leftover perf sink lives in the Dev plugin's collectible context; clearing the slot
        // and the cached invocation list keeps this generation from pinning it. NoteCoreUninstall
        // also latches WHY the slot emptied, so a Dev capture that outlives this swap stops itself
        // with stopReason=core-swap instead of running on with a dispatch that will never tick
        // again (R28 findings 14 and 17).
        try { Dev.PerfProbe.NoteCoreUninstall(); }
        catch (Exception e) { Logger?.LogWarning($"PerfProbe.NoteCoreUninstall: {e.Message}"); }
        BootstrapLink.SetLifetimeQuitting(null);
        BootstrapLink.SetLifetimeFrame(null);
        HarmonySlotHandoff.ClearLateFrame();
        ResetLocalizationHookFlag();
        return true;
    }

    /// <summary>
    /// Pin dump after plugin sweep: Frame / EventBus / SystemHooks counts, leftover Harmony
    /// owners, and any Core-owned MainThread work still queued.
    /// </summary>
    internal static void DiagnosePins()
    {
        var log = Logger;
        if (log == null) return;
        try
        {
            int frame = Frame?.GetInvocationList().Length ?? 0;
            log.LogInfo("pin-diag Plugin.Frame handlers=" + frame);
        }
        catch (Exception e) { log.LogWarning("pin-diag Plugin.Frame: " + e.Message); }
        try { log.LogInfo("pin-diag EventBus " + Events.EventBus.Describe()); }
        catch (Exception e) { log.LogWarning("pin-diag EventBus: " + e.Message); }
        try { log.LogInfo("pin-diag SystemHooks " + Hooks.SystemHooks.DescribeCounts()); }
        catch (Exception e) { log.LogWarning("pin-diag SystemHooks: " + e.Message); }
        try { log.LogInfo(Dev.MainThread.DescribeOwned("progenitor-core", typeof(Plugin).Assembly.FullName)); }
        catch (Exception e) { log.LogWarning("pin-diag MainThread: " + e.Message); }
        try { Reload.HarmonyFallback.DiagnosePins(); }
        catch (Exception e) { log.LogWarning("pin-diag Harmony: " + e.Message); }
        try { log.LogInfo(Reload.JsonCacheSweep.DescribeProcessCaches()); }
        catch (Exception e) { log.LogWarning("pin-diag Json: " + e.Message); }
        DiagnoseModulePins(log);
        // Plugins unloaded by an earlier reload whose contexts are still alive: name what holds them.
        // Contexts this swap just unloaded are skipped; they have not had a collect yet.
        try { Reload.PluginPinHolders.Diagnose(log.LogInfo, minAgeSeconds: 5); }
        catch (Exception e) { log.LogWarning("pin-diag plugin-holders: " + e.Message); }
    }

    private static void DiagnoseModulePins(ManualLogSource log)
    {
        LogModulePins(log, HarmonySlotHandoff.EventsModule, Events.EventHooks.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.HooksModule, Hooks.SystemHooks.DescribePins, dropZeroCounts: true);
        LogModulePins(log, HarmonySlotHandoff.CommandsModule, Commands.CommandSystem.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.ContentModule, Content.PrefabRegistry.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.ConfigModule, Progenitor.Config.ConfigHelper.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.PersistenceModule, Persistence.ModSave.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.HostedModule, Hosted.HostedServerLauncher.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.LocalizationModule, Localization.Localization.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.NetworkingModule, DescribeNetworkingPinsWithoutContent);
        LogModulePins(log, HarmonySlotHandoff.ChatModule, Chat.Chat.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.CastlesModule, Castles.Castles.DescribePins);
        LogModulePins(log, Combat.CombatHooks.ModuleName, Combat.CombatHooks.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.SystemsModule, Systems.VirtualSystems.DescribePins, dropZeroCounts: true);
        LogModulePins(log, HarmonySlotHandoff.PlayersModule, Players.PlayerControl.DescribePins);
        LogModulePins(log, HarmonySlotHandoff.EcsModule, Ecs.ComponentIndex.DescribePins);
        LogModulePins(log, Effects.Assets.ModuleName, Effects.Assets.DescribePins);
        LogModulePins(log, Effects.ServerBeam.ModuleName + ".ServerBeam", Effects.ServerBeam.DescribePins);
        LogModulePins(log, Progenitor.Ui.Ui.ModuleName, Progenitor.Ui.Ui.DescribePins);
        LogModulePins(log, Units.Units.ModuleName, Units.Units.DescribePins);
        LogModulePins(log, Buffs.Buffs.ModuleName, Buffs.Buffs.DescribePins, dropZeroCounts: true);
        LogModulePins(log, Units.Units.ModuleName + ".Looks", Units.UnitLooks.DescribePins);
        LogModulePins(log, Units.Units.ModuleName + ".HealthBars", Units.UnitHealthBars.DescribePins);
        LogModulePins(log, "Ai.InertClones", Ai.InertClones.DescribePins);
    }

    private static void LogModulePins(ManualLogSource log, string module, Func<string> describe, bool dropZeroCounts = false)
    {
        try
        {
            if (HarmonySlotHandoff.ShouldSkipModule(module))
            {
                log.LogInfo(HarmonySlotHandoff.FormatModulePinsSkipped(module));
                return;
            }
            string line = describe();
            if (dropZeroCounts)
                line = HarmonySlotHandoff.DropZeroCountPinHolds(line);
            log.LogInfo(line);
        }
        catch (Exception e) { log.LogWarning("pin-diag module=" + module + ": " + e.Message); }
    }

    /// <summary>
    /// Handshake's pin dump plus CustomEvents counts for Handshake event ids only.
    /// PrefabRegistry's PrfD/PrfU/PrfK handlers belong to Content, not Networking.
    /// </summary>
    private static string DescribeNetworkingPinsWithoutContent()
    {
        string line = Networking.Handshake.DescribePins();
        int client = CountCustomEventHandlers(client: true, handshakeOnly: true);
        int server = CountCustomEventHandlers(client: false, handshakeOnly: true);
        line = HarmonySlotHandoff.ReplaceOrDropHold(
            line,
            "clientHandlers=",
            client > 0 ? "clientHandlers=" + client.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        line = HarmonySlotHandoff.ReplaceOrDropHold(
            line,
            "serverHandlers=",
            server > 0 ? "serverHandlers=" + server.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        return line;
    }

    private static int CountCustomEventHandlers(bool client, bool handshakeOnly)
    {
        try
        {
            var type = typeof(Networking.CustomEvents);
            var field = type.GetField(
                client ? "ClientHandlers" : "ServerHandlers",
                BindingFlags.NonPublic | BindingFlags.Static);
            var dict = field?.GetValue(null) as System.Collections.IDictionary;
            if (dict == null) return 0;
            int n = 0;
            foreach (var key in dict.Keys)
            {
                if (key is not int id) continue;
                if (handshakeOnly && !IsHandshakeEventId(id)) continue;
                n++;
            }
            return n;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsHandshakeEventId(int eventId) =>
        eventId == Networking.Handshake.RequestEventId
        || eventId == Networking.Handshake.ResponseEventId
        || eventId == Networking.Handshake.ResultEventId;

    private static void ResetLocalizationHookFlag()
    {
        try
        {
            var field = typeof(Localization.Localization).GetField("_hooked", BindingFlags.NonPublic | BindingFlags.Static);
            field?.SetValue(null, false);
        }
        catch (Exception e)
        {
            Logger?.LogWarning("Localization _hooked reset: " + e.Message);
        }
    }

    /// <summary>
    /// Drop <see cref="Frame"/> subscribers owned by this plugin instance or declared in its
    /// assembly. Sweep calls this so a leftover handler cannot pin a collectible ALC.
    /// </summary>
    internal static int RemoveFrameHandlers(object owner)
    {
        var frame = Frame;
        if (frame == null || owner == null) return 0;
        var asm = owner as Assembly ?? owner.GetType().Assembly;
        int n = 0;
        foreach (var d in frame.GetInvocationList())
        {
            if (d is not Action a) continue;
            if (!DelegateOwnedBy(a, owner, asm)) continue;
            Frame -= a;
            n++;
        }
        return n;
    }

    private static bool DelegateOwnedBy(Delegate d, object owner, Assembly? asm)
    {
        if (ReferenceEquals(d.Target, owner)) return true;
        if (asm == null) return false;
        if (d.Method.DeclaringType?.Assembly == asm) return true;
        return d.Target != null && d.Target.GetType().Assembly == asm;
    }

    private static bool LiveWorldExists()
    {
        try
        {
            var server = Worlds.Server;
            if (server != null && server.IsCreated) return true;
            var client = Worlds.Client;
            if (client != null && client.IsCreated) return true;
        }
        catch
        {
            // Interop miss must not look like "no world" and silently skip against a live save.
            return true;
        }
        return false;
    }

    private static bool HostedStartIsInFlight()
    {
        try
        {
            var field = typeof(Hosted.HostedServerLauncher).GetField("Strip", BindingFlags.NonPublic | BindingFlags.Static);
            var strip = field?.GetValue(null);
            if (strip == null) return false;
            return strip.GetType().GetProperty("HasInFlightStart")?.GetValue(strip) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Process role, decided once from the executable name. The bundled server a client launches
    /// for a private game is VRisingServer.exe as well, so dedicated and hosted play get the same
    /// answer, which is what the spec requires.
    /// </summary>
    public static bool IsServerProcess { get; } =
        Process.GetCurrentProcess().ProcessName.StartsWith("VRisingServer", StringComparison.OrdinalIgnoreCase);

    private static int _lastFrame = -1;

    /// <summary>Runs the framework tick at most once per Unity frame, whichever source fires first.</summary>
    internal static void FrameTick()
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;
        Tick(frame);
    }

    private static void Tick(int frame)
    {
        if (!BootstrapLink.OwnsLifetime)
        {
            Dev.MainThread.Drain();
            Reload.PluginReloader.Tick();
        }
        Dev.MainThread.NoteFrame(frame);
        Progenitor.Ui.UiFrameCost.BeginFrame();
        long start = Stopwatch.GetTimestamp();
        _dispatchFrame = frame;
        try { DispatchFrame(Dev.PerfProbe.Sink); }
        finally
        {
            _dispatchFrame = -1;
            Progenitor.Ui.UiFrameCost.EndFrame(start);
        }
    }

    private static int _dispatchFrame = -1;

    /// <summary>
    /// The Unity frame number while <see cref="Frame"/> handlers are being dispatched, read once by
    /// <see cref="FrameTick"/>. Outside the dispatch it answers false and callers read
    /// <c>Time.frameCount</c> themselves. <c>Time.frameCount</c> goes through
    /// <c>il2cpp_runtime_invoke</c>, which boxes the int on the IL2CPP heap, so per-key or
    /// per-handler reads add up.
    /// </summary>
    internal static bool TryDispatchFrame(out int frame)
    {
        frame = _dispatchFrame;
        return frame >= 0;
    }

    /// <summary>
    /// Time each <see cref="Frame"/> subscriber. Named UI phases stamp themselves;
    /// the hitch line's <c>handler=</c> is the slowest subscriber on that frame,
    /// including a queued directory refresh that runs after the triggering command
    /// has already returned. A handler that throws no longer skips the rest.
    /// </summary>
    private static void DispatchFrame(Dev.IPerfSink? sink)
    {
        long start = Stopwatch.GetTimestamp();
        var list = Dev.PerfProbe.InvocationList(Frame);
        for (int i = 0; i < list.Length; i++)
        {
            if (sink != null)
            {
                try { sink.BeginCallback(); }
                catch { /* a profiler must never break a frame */ }
            }
            long handlerStart = Stopwatch.GetTimestamp();
            try
            {
                if (list[i] is Action action) action();
            }
            catch (Exception e) { Logger?.LogError($"Frame handler threw: {e}"); }
            long ticks = Stopwatch.GetTimestamp() - handlerStart;
            // TEXTAREA2: the declaring type too ("NativeModsEntry.Tick", not "Tick": several
            // subscribers share a method name). Built only when it is this frame's worst.
            if (Progenitor.Ui.UiFrameCost.TicksToMs(ticks) > Progenitor.Ui.UiFrameCost.WorstHandlerMs)
            {
                string handlerName = "";
                try
                {
                    var method = list[i].Method;
                    handlerName = method == null ? "" : (method.DeclaringType?.Name ?? "") + "." + method.Name;
                }
                catch { handlerName = ""; }
                Progenitor.Ui.UiFrameCost.NoteHandler(handlerName, ticks);
            }
            if (sink == null) continue;
            try { sink.FrameHandler(list[i], ticks); }
            catch { /* a profiler must never break a frame */ }
        }

        if (sink == null) return;
        try { sink.CoreTick(Stopwatch.GetTimestamp() - start, start); }
        catch { /* same */ }
    }

}
