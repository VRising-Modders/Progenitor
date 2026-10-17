using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using BepInEx.Logging;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Reload;
using Unity.Entities;

namespace Progenitor.Dev;

/// <summary>
/// The operations. Each returns a plain object that serializes to JSON. Main-thread ops touch
/// worlds and entities; the others are thread-safe on their own. This type holds no pending-op
/// state: the one-in-flight slot, deferred waiter, and unload abort live on
/// <see cref="MainThreadOpGate"/> / <see cref="DeferredResult"/> in the owning <c>Bridge</c>
/// so a Core reload that unloads Dev can fail them without this class surviving as a static.
/// </summary>
internal sealed class Handlers
{
    private readonly MainThreadOpGate _gate;

    internal Handlers(MainThreadOpGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        Ops.FreezeDump.Start();
    }

    /// <summary>Stops the freeze poller. Does not join (Unload must not wait on a dump write).</summary>
    internal void StopWatchdog() => Ops.FreezeDump.Stop();

    private static readonly HashSet<string> MainThreadOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping", "reload", "users", "worlds", "command", "chat_observe", "broadcast", "ghost", "rig_reference",
        "query", "get", "set", "components", "spawn", "entity", "prefab", "buff", "heal", "damage", "kill", "downed", "respawn", "create_character", "wake", "headless", "bot", "bot_look", "bot_cast", "bot_inventory", "bot_path", "bot_daytime", "bot_quests", "bot_event", "bot_cost", "bot_territories", "bot_use", "bot_recipes", "bot_blueprints", "bot_canplace", "bot_prefabs", "bot_threats", "bot_station", "bot_exit_graveyard", "bot_repair", "equip", "players",
        "teleport", "give", "unlock", "engage", "protect", "bind", "inspect", "tree", "snapshot", "restore",
        "status", "save", "world_save", "core_reload", "reload_status", "store", "connect", "host", "host_capture", "disconnect", "quit", "castles",
        "capture", "perf", "mods_ui", "keybind_press", "map", "key", "mouse", "nav", "build_search", "materials", "servants", "inventory_stamp", "dye", "remote_install",
        "ui_input", "ui_read", "display", "screenshot", "console",
        "catalog_residency", "catalog_load", "catalog_status", "catalog_preview", "catalog_release",
        "freeze_hold", "time", "photo", "locate", "container_preview", "camera_preview", "setting", "join_ui", "messages_record", "item_catalog",
        "join_admin", "server_desc", "server_rules", "world_backups", "server_browser", "auto_update", "chat_tabs", "chat_history",
    };

    public bool IsMainThreadOp(string op) => MainThreadOps.Contains(op);

    public object? Invoke(string op, JsonElement args)
    {
        switch (op.ToLowerInvariant())
        {
            case "ping": return Ping();
            case "logs": return Logs(args);
            case "reload": return Reload();
            case "users": return Users();
            case "worlds": return Worlds();
            case "command": return Command(args);
            case "chat_observe": return Ops.ChatObserve.Run(args);
            case "broadcast": return Ops.ChatOps.Run(args);
            case "ghost": return Ops.GhostOps.Run(args);
            case "query": return Ops.EntityOps.Query(RequireWorld(args), args);
            case "get": return Ops.EntityOps.Get(RequireWorld(args), args);
            case "set": return Ops.EntityOps.Set(RequireWorld(args), args);
            case "components": return Ops.EntityOps.Components(RequireWorld(args), args);
            case "rig_reference": return Ops.RigReferenceOps.Run(args);
            case "spawn": return Ops.EntityOps.Spawn(RequireWorld(args), args);
            case "entity": return Ops.EntityLifecycleOps.Run(RequireWorld(args), args);
            case "prefab": return Ops.EntityOps.Prefab(RequireWorld(args), args);
            case "buff": return Ops.BuffOps.Run(RequireWorld(args), args);
            case "heal": return Ops.HealOps.Run(RequireWorld(args), args);
            case "damage": return Ops.LifeOps.Damage(RequireWorld(args), args);
            case "kill": return Ops.LifeOps.Kill(RequireWorld(args), args);
            case "downed": return Ops.LifeOps.Downed(RequireWorld(args), args);
            case "create_character": return Ops.CharacterCreateOps.Run(RequireWorld(args), args);
            case "wake": return Ops.WakeOps.Run(RequireWorld(args), args);
            case "headless": return Ops.HeadlessProfile.Status();
            // Experimental, early-development headless bot ops (tools/bots/README.md): development-only, not a supported feature.
            case "bot": return Bots.BotDriver.Run(RequireWorld(args), args);
            case "bot_look": return Bots.BotLook.Run(RequireWorld(args), args);
            case "bot_cast": return Bots.BotCast.Run(RequireWorld(args), args);
            case "bot_inventory": return Bots.BotInventory.Run(RequireWorld(args), args);
            case "bot_path": return Bots.BotPath.Run(RequireWorld(args), args);
            case "bot_daytime": return Bots.BotCast.Daytime(RequireWorld(args));
            case "bot_quests": return Bots.BotQuests.Run(RequireWorld(args), args);
            case "bot_event": return Bots.BotEvents.Run(RequireWorld(args), args);
            case "bot_cost": return Bots.BotInventory.Cost(RequireWorld(args), args);
            case "bot_territories": return Bots.BotTerritories.Run(RequireWorld(args), args);
            case "bot_use": return Bots.BotInventory.Use(RequireWorld(args), args);
            case "bot_recipes": return Bots.BotRecipes.Run(RequireWorld(args), args);
            case "bot_blueprints": return Bots.BotBlueprints.Run(RequireWorld(args), args);
            case "bot_canplace": return Bots.BotBlueprints.CanPlace(RequireWorld(args), args);
            case "bot_prefabs": return Bots.BotBlueprints.Prefabs(RequireWorld(args), args);
            case "bot_threats": return Bots.BotThreats.Run(RequireWorld(args), args);
            case "bot_station": return Bots.BotStation.Run(RequireWorld(args), args);
            case "bot_exit_graveyard": return Bots.BotGraveyardExit.Run(RequireWorld(args), args);
            case "bot_repair": return Bots.BotRepair.Run(RequireWorld(args), args);
            // Enqueues CharacterRespawnEvent. Does not call RespawnCharacter.
            case "respawn": return Ops.RespawnOps.Run(RequireWorld(args), args);
            case "equip": return Ops.EquipOps.Run(RequireWorld(args), args);
            case "teleport": return Ops.TeleportOps.Run(RequireWorld(args), args);
            case "give": return Ops.GiveOps.Run(RequireWorld(args), args);
            // TEST SETUP: unlock blueprints/recipes for one player (isolated test server only).
            case "unlock": return Ops.UnlockOps.Run(RequireWorld(args), args);
            // BUILDSEARCH1: the search box Core adds to the game's build menu (client).
            case "build_search": return Ops.BuildSearchOps.Run(args);
            // ctx.MaterialSource seams: status on either side; a one-shot fault injection on the server.
            case "materials": return Ops.MaterialsOps.IsSend(args) ? Ops.MaterialEventOps.Send(RequireWorld(args), args) : Ops.MaterialsOps.Run(args);
            case "engage": return Ops.EngageOps.Run(RequireWorld(args), args);
            case "protect": return Ops.ProtectOps.Run(RequireWorld(args), args);
            case "bind": return Ops.BindOps.Run(RequireWorld(args), args);
            // Read the whole entity in one round trip (components + get for every type).
            case "inspect": return Ops.InspectOps.Run(RequireWorld(args), args);
            case "tree": return Ops.TreeOps.Run(RequireWorld(args), args);
            // Undo an experiment on the test server. Not a save: see SnapshotOps.
            case "snapshot": return Ops.SnapshotOps.Snapshot(RequireWorld(args), args);
            case "restore": return Ops.SnapshotOps.Restore(RequireWorld(args), args);
            case "players": return Ops.EntityOps.Players(RequireWorld(args), args);
            case "status": return Ops.StatusOps.Status();
            case "save": return Ops.StatusOps.Save();
            case "world_save": return Ops.WorldSaveOps.Run(args);
            case "core_reload": return Ops.CoreReloadOps.Run(args);
            case "reload_status": return Ops.CoreReloadOps.Status();
            case "store": return Ops.StatusOps.Store(args);
            case "connect": return Ops.ConnectOps.Connect(args);
            case "host": return Ops.ConnectOps.Host(args);
            case "host_capture": return Ops.HostCapture.Run(args);
            case "disconnect": return Ops.ConnectOps.Disconnect(args);
            case "quit": return Ops.QuitOps.Quit();
            case "castles": return Ops.CastleOps.Run(RequireWorld(args), args);
            // Server, main thread: a castle's servants and hunts, a send, finish a hunt now, the servant-timers setting (ServantBridge).
            case "servants": return Ops.ServantOps.Run(args);
            // Server, main thread, read only: ctx.Inventory.ChangeStamp of named containers, the clock and the audit counters.
            case "inventory_stamp": return Ops.InventoryStampOps.Run(args);
            case "time": return Ops.TimeOps.Run(args);
            case "photo": return Ops.PhotoOps.Run(args);
            // Client, main thread: a ctx.Hud.Locate ping at a world point, and every ping's last drawn frame (HudLocateBridge).
            case "locate": return Ops.LocateOps.Run(args);
            case "container_preview": return Ops.ContainerPreviewOps.Run(args);
            case "camera_preview": return Progenitor.Cameras.CameraPreviewBridge.Readback();
            // Both sides, read-only: ctx.Items.Catalog() of this process (count, build time, filtered rows).
            case "item_catalog": return Ops.ItemCatalogOps.Run(args);
            // Client-side observation window over the game's own sequencer/beam draw. Takes no
            // world argument: it resolves Worlds.Client itself and refuses on the server.
            case "capture": return Ops.CaptureOps.Run(args);
            // Both sides: time Progenitor's own per-frame main-thread work through the Core
            // PerfProbe seam. Takes no world argument; it works from the menu and from boot.
            case "perf": return Ops.PerfOps.Run(args);
            // Socket thread (not in MainThreadOps). Scalar progress only; no Unity APIs, no Gate.
            case "perf_status": return Ops.PerfOps.LiveStatus();
            // Client-side Mods workspace. Takes no world argument; refuses on the server.
            case "mods_ui": return Ops.ModsUiOps.Run(args);
            // Client, main thread. Presses a registered keybind through Core's real dispatch path.
            case "keybind_press": return Ops.KeybindPressOps.Run(args);
            // Client, main thread. Opens, closes or reads the game's world map through the calls its M key ends in.
            case "map": return Ops.MapOps.Run(args);
            // Client, main thread. Queues keyboard state and text events into the Input System (focus-free key input).
            case "key": return Ops.KeyInjectOps.Run(args);
            case "mouse": return Ops.MouseInjectOps.Run(args);
            // NAV1. Client, main thread: the controller focus (state), one navigation step (press), and a virtual
            // gamepad's button press through the real device read (pad, pad_remove).
            case "nav": return Ops.NavOps.Run(args);
            // BRIDGEUI1. Client, main thread: synthetic pointer, wheel, UI-layer keys and typing at the UI kit's own input
            // layer (no OS input, no window focus); its read-only widget readback; resolution through the game's graphics
            // path; and an in-process screenshot.
            case "ui_input": return Ops.UiInputOps.Run(args);
            case "ui_read": return Ops.UiReadOps.Run(args);
            case "console": return Ops.ConsoleOps.Run(args);
            case "display": return Ops.DisplayOps.Run(args);
            case "screenshot": return Ops.DisplayOps.Screenshot(args);
            // Client, main thread, read only: Dye all's scan, picker, watches and last hold (DyeAllTestBridge).
            case "dye": return Ops.DyeOps.Run(args);
            // TEXTLIMIT1. Main thread, both sides: a synced setting's stored value on this side, the
            // server's settings audit tail, and (client) an administrator edit sent as Apply sends it.
            case "setting": return Ops.SettingOps.Run(args);
            // Mod compatibility C5. Main thread, both sides: record a mod's ctx.Messages traffic as replay fixtures.
            case "messages_record": return Ops.MessagesRecordOps.Run(args);
            // SERVERDESC1. Main thread, both sides: the server-list description (rows, composed text, live value,
            // Steam keys, settings files); server: set the rows and apply as an admin edit does.
            case "server_desc": return Ops.ServerDescOps.Run(args);
            case "chat_tabs": return Ops.ChatTabsOps.Run(args);
            case "chat_history": return Ops.ChatHistoryOps.Run(args);
            case "server_browser": return Ops.ServerBrowserOps.Run(args);
            // JOINFLOW1. Client, main thread: read and drive the join screens (server message, mods page,
            // Thunderstore confirmation) through the buttons' own handlers.
            case "join_ui": return Ops.JoinUiOps.Run(args);
            case "join_admin": return Ops.JoinAdminOps.Run(args);
            // Settings organization stages 6 and 7. Main thread: the Server rules (client: read, get, write as the page
            // sends it; server: read the files, layers, loaded values and overrides).
            case "server_rules": return Ops.ServerRulesOps.Run(args);
            case "world_backups": return Ops.WorldBackupsOps.Run(args);
            case "auto_update": return Ops.AutoUpdateOps.Run(args);
            // HOT1. Socket thread: reports and re-merges the local package index ([Install]
            // LocalIndexPath). Reads files and plain catalog data; no world, no download, no write.
            case "install_local": return Ops.InstallOps.Run(args);
            // REMOTEINST1. Main thread: reads and drives the admin remote install channel on either
            // side (client: route, status, send; server: settings, probe, rows, audit tail).
            case "remote_install": return Ops.RemoteInstallOps.Run(args);
            case "catalog_residency": return Ops.CatalogAssetOps.Residency(args);
            case "catalog_load": return Ops.CatalogAssetOps.Load(args);
            case "catalog_status": return Ops.CatalogAssetOps.Status(args);
            case "catalog_preview": return Ops.CatalogAssetOps.Preview(args);
            case "catalog_release": return Ops.CatalogAssetOps.Release(args);
            // Socket thread. Never takes the one-op gate. Liveness probe after a main-thread timeout.
            // Poll may start a dump writer thread; MiniDumpWriteDump never runs here.
            case "bridge_status":
                Ops.FreezeDump.Poll();
                return Ops.BridgeStatus.Run(_gate, args);
            // Socket thread. Starts a dedicated dump writer; returns {started, path} immediately.
            case "dump": return Ops.FreezeDump.Run(args);
            // Main thread. Bounded synthetic occupancy for watchdog validation. Requires synthetic=true.
            case "freeze_hold": return Ops.FreezeHoldOps.Run(args);
            case "ops": return new { ops = new[] { "ping", "logs", "bridge_status", "bridge_users", "dump", "freeze_hold", "reload", "users", "worlds", "command", "chat_observe", "broadcast", "ghost", "core_reload", "reload_status", "query", "get", "set", "components", "spawn", "entity", "prefab", "buff", "heal", "damage", "kill", "downed", "respawn", "create_character", "wake", "headless", "bot", "bot_look", "bot_cast", "bot_inventory", "bot_path", "bot_daytime", "bot_quests", "bot_event", "bot_cost", "bot_territories", "bot_use", "bot_recipes", "bot_blueprints", "bot_canplace", "bot_prefabs", "bot_threats", "bot_station", "bot_exit_graveyard", "bot_repair", "equip", "teleport", "give", "unlock", "engage", "protect", "bind", "inspect", "tree", "snapshot", "restore", "players", "status", "save", "world_save", "store", "connect", "host", "host_capture", "disconnect", "quit", "castles", "time", "photo", "locate", "container_preview", "camera_preview", "item_catalog", "capture", "perf", "perf_status", "mods_ui", "keybind_press", "map", "key", "mouse", "nav", "ui_input", "ui_read", "display", "screenshot", "console", "build_search", "materials", "servants", "inventory_stamp", "dye", "catalog_residency", "catalog_load", "catalog_status", "catalog_preview", "catalog_release", "install_local", "remote_install", "setting", "messages_record", "ops" } };
            default: throw new ArgumentException($"unknown op '{op}'");
        }
    }

    /// <summary>The side's main world (Server or Client_0), or a named one via args.world.</summary>
    private static World RequireWorld(JsonElement args)
    {
        var name = Get<string?>(args, "world", null);
        var world = name != null ? Progenitor.Worlds.Find(name) : (Progenitor.Plugin.IsServerProcess ? Progenitor.Worlds.Server : Progenitor.Worlds.Client);
        return world ?? throw new InvalidOperationException(name != null ? $"no world named '{name}'" : "the game world does not exist yet (not in a game)");
    }

    private static object Ping()
    {
        var worlds = new List<string>();
        var all = World.s_AllWorlds;
        if (all != null)
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].IsCreated) worlds.Add(all[i].Name);
        string? gameIdentity = null;
        object? images = null;
        try { gameIdentity = Ops.PingIdentity.TryLiveIdentity(typeof(Progenitor.Plugin).Assembly); }
        catch { gameIdentity = null; }
        try { images = Ops.PingIdentity.TryLiveImages(); }
        catch { images = null; }

        return new
        {
            side = Progenitor.Plugin.IsServerProcess ? "server" : "client",
            progenitor = Progenitor.Plugin.Version,
            uptimeSeconds = (int)(DateTime.UtcNow - Progenitor.Plugin.StartedUtc).TotalSeconds,
            worlds,
            reloadFolder = PluginReloader.Folder,
            reloadedPlugins = PluginReloader.LoadedPluginNames(),
            logSeq = LogBuffer.Instance.LatestSeq,
            reload = Ops.CoreReloadOps.TryDescribe(),
            gameIdentity,
            images,
        };
    }

    private static object Logs(JsonElement args)
    {
        long since = Get(args, "since", -1L);
        int max = Math.Clamp(Get(args, "max", 200), 1, 2000);
        string? filter = Get<string?>(args, "filter", null);
        string? level = Get<string?>(args, "level", null);
        LogLevel? min = level == null ? null : Enum.TryParse<LogLevel>(level, true, out var lv) ? lv : null;

        var entries = LogBuffer.Instance.Read(since, max, filter, min);
        var lines = new List<object>(entries.Count);
        foreach (var e in entries)
            lines.Add(new { seq = e.Seq, time = e.Time.ToString("HH:mm:ss.fff"), level = e.Level.ToString(), source = e.Source, text = e.Text });
        return new { latestSeq = LogBuffer.Instance.LatestSeq, count = lines.Count, lines };
    }

    private static object Reload()
    {
        var events = PluginReloader.RescanNow();
        return new { events, loaded = PluginReloader.LoadedPluginNames() };
    }

    private static object Worlds()
    {
        var list = new List<object>();
        var all = World.s_AllWorlds;
        if (all != null)
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                if (w == null || !w.IsCreated) continue;
                list.Add(new { name = w.Name, entities = w.EntityManager.UniversalQuery.CalculateEntityCount() });
            }
        return new { worlds = list };
    }

    private static object Users()
    {
        var world = Progenitor.Worlds.Server ?? Progenitor.Worlds.Client;
        if (world == null) return new { users = Array.Empty<object>(), note = "no world yet" };
        var em = world.EntityManager;
        var entities = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        var users = new List<object>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            var u = em.GetComponentData<User>(entity);
            users.Add(new
            {
                index = u.Index,
                name = u.CharacterName.ToString(),
                connected = u.IsConnected,
                admin = u.IsAdmin,
                platformId = u.PlatformId.ToString(),
                entity = $"{entity.Index}:{entity.Version}",
                character = $"{u.LocalCharacter._Entity.Index}:{u.LocalCharacter._Entity.Version}",
                handshake = HandshakeStatus(u.Index),
            });
        }
        return new { users };
    }

    /// <summary>
    /// Server handshake answer state. Tokens:
    /// <c>none</c> — not waiting (never asked, ghost/hosted child, timeout disabled,
    /// or disconnected). This is the "nothing in flight" token; it is not <c>asked</c>.
    /// <c>asked</c> — waiting for an answer (renamed from <c>pending</c>).
    /// <c>ok</c> — matched.
    /// <c>mismatch</c> — answered and failed the required list.
    /// <c>timeout</c> — deadline fired.
    /// <c>awaiting-install</c> — the answer failed only on mods this server offers; the kick is deferred for the install window (FMAN18).
    /// <c>unverified</c> — connected, and nothing on the server requires an answer.
    /// Includes disabled User entities so a connected extra-instance that is disabled
    /// still shows <c>asked</c>.
    /// </summary>
    private static string HandshakeStatus(int userIndex)
    {
        try { return Progenitor.Networking.Handshake.UserAnswerStatus(userIndex); }
        catch { return "none"; }
    }

    /// <summary>
    /// Injects a chat message as if a connected user typed it. Anything that hooks the chat system
    /// (Progenitor commands, spike commands, another chat-command framework) sees it. Server only.
    /// Replies go to that user's client and to the log if the handler logs them.
    /// <c>user</c> is a character name or a numeric <c>User.Index</c>; <c>userIndex</c> wins when
    /// both are set. Ghosts created by the <c>ghost</c> op are <c>IsConnected</c> and resolve here
    /// the same way a real player does (verified live: <c>.joins</c> as Ghost1 returned Executed).
    /// </summary>
    private static object Command(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("command runs on the server side only");
        var request = Ops.ChatInjectArgs.Parse(args);
        var injected = Ops.ChatObserve.Inject(request.Text, request.UserIndex, request.CharacterName,
            (ProjectM.Network.ChatMessageType)request.MessageType, request.Echo, out bool echoed);
        return new
        {
            injectedAs = injected.InjectedAs,
            userIndex = injected.UserIndex,
            text = injected.RequestedText,
            channel = request.Channel,
            echoed,
        };
    }

    private static T Get<T>(JsonElement args, string name, T fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        try
        {
            if (typeof(T) == typeof(string)) return (T)(object?)el.GetString()!;
            if (typeof(T) == typeof(int)) return (T)(object)el.GetInt32();
            if (typeof(T) == typeof(long)) return (T)(object)el.GetInt64();
            if (typeof(T) == typeof(bool)) return (T)(object)el.GetBoolean();
        }
        catch { }
        return fallback;
    }
}
