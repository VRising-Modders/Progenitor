using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Progenitor.Buffs;
using Progenitor.Castles;
using Progenitor.Commands;
using Progenitor.Config;
using Progenitor.Content;
using Progenitor.Effects;
using Progenitor.Events;
using Progenitor.Items;
using Progenitor.Players;
using Progenitor.Units;
using Progenitor.Hooks;
using Progenitor.Networking;
using Progenitor.Persistence;
using Progenitor.Systems;
using Progenitor.Triggers;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Plugins;

/// <summary>
/// Per-plugin bag of state and registrations. Mods put mutable data on a derived context and go
/// through its helpers so <see cref="ProgenitorPlugin{TContext}"/> can reverse everything on
/// unload — that is what makes hot reload possible without each author writing <c>Unload</c>.
/// Constructed by the base plugin; do not new this up yourself, and keep the constructor empty
/// (Log, Harmony, and the wrappers are attached after <c>new</c>, before <c>OnLoad</c>).
/// </summary>
public class PluginContext
{
    private readonly DisposableBag _bag = new();
    private ManualLogSource? _log;
    private PluginConfig? _config;
    private PluginEvents? _events;
    private PluginHooks? _hooks;
    private PluginCommands? _commands;
    private PluginContent? _content;
    private PluginNet? _net;
    private PluginHandshake? _handshake;
    private PluginSystems? _systems;
    private PluginCastles? _castles;
    private PluginChat? _chat;
    private PluginEffects? _effects;
    private PluginUnits? _units;
    private PluginItems? _items;
    private PluginDebug? _debug;
    private PluginBuffs? _buffs;
    private PluginPlayers? _players;
    private PluginTriggers? _triggers;
    private Progenitor.Ui.PluginMenu? _menu;

    /// <summary>Register owned settings groups and custom pages in the Progenitor Mods menu.</summary>
    public Progenitor.Ui.PluginMenu Menu { get { EnsureAttached(); return _menu ??= new Progenitor.Ui.PluginMenu(this); } }

    private Progenitor.Ui.PluginSettings? _settings;

    /// <summary>
    /// SETTINGS1 stage 2: lay out this plugin's settings on its Mods page (<c>ctx.Settings.Page(page =&gt; …)</c>),
    /// bound to its config entries. Tracked: every unload reason drops the page. See <c>docs/mod-settings.md</c>.
    /// </summary>
    public Progenitor.Ui.PluginSettings Settings { get { EnsureAttached(); return _settings ??= new Progenitor.Ui.PluginSettings(this); } }

    private Progenitor.Ui.PluginDialogs? _dialogs;

    /// <summary>
    /// DIALOGS1: ready-made modal dialogs in the framework's own dialog layout — <c>Confirm</c>,
    /// <c>Notice</c>, <c>Choose</c>, <c>Prompt</c>. One on screen at a time, later ones queued; each
    /// returns a handle that closes it. Client only. Tracked: every unload reason drops this plugin's
    /// dialogs without running their callbacks. Named <c>Dialogs</c>, not <c>Ui</c>, because a
    /// <c>Ui</c> member would hide the static <c>Progenitor.Ui.Ui</c> class inside a plugin's own
    /// context subclass. See <c>docs/ui.md</c> "Dialogs".
    /// </summary>
    public Progenitor.Ui.PluginDialogs Dialogs { get { EnsureAttached(); return _dialogs ??= new Progenitor.Ui.PluginDialogs(this); } }

    private Progenitor.Ui.PluginHud? _hud;

    /// <summary>
    /// P7: notifications in the game's announcement style, a Locate ping on a world entity, and a pick-in-world mode.
    /// Client only; tracked on every unload reason. See <c>docs/ui.md</c> "HUD: notifications, locate and pick in world".
    /// </summary>
    public Progenitor.Ui.PluginHud Hud { get { EnsureAttached(); return _hud ??= new Progenitor.Ui.PluginHud(this); } }

    private Progenitor.Ui.PluginWindows? _windows;

    /// <summary>
    /// WINDOWS1: this plugin's standard windows — <c>ctx.Windows.Create(UiWindowPreset.Basic | Header | Tabbed, title,
    /// options)</c> — with the frame, title, version stamp, input mode and Escape behavior fixed by the preset. Client only;
    /// tracked on every unload reason. See <c>docs/ui.md</c> "Standard windows".
    /// </summary>
    public Progenitor.Ui.PluginWindows Windows { get { EnsureAttached(); return _windows ??= new Progenitor.Ui.PluginWindows(this); } }
    private IModStore? _save;
    private IModStore? _serverSave;
    private Harmony? _harmony;
    private BasePlugin? _plugin;
    private Assembly? _assembly;
    private Side _side;
    private bool _attached;

    /// <summary>This plugin's BepInEx log. Named after the plugin so its lines are greppable in a mixed log.</summary>
    public ManualLogSource Log { get { EnsureAttached(); return _log!; } }

    /// <summary>Typed binds over this plugin's BepInEx <see cref="ConfigFile"/> (<c>GUID.cfg</c>).</summary>
    public PluginConfig Config { get { EnsureAttached(); return _config!; } }

    /// <summary>
    /// Bind a setting in this plugin's <c>GUID.cfg</c>. When <paramref name="synced"/> is true
    /// the server pushes the value to clients that also have this plugin after a successful
    /// handshake; the client locks the entry until disconnect and then restores its local
    /// value. See <c>docs/config-sync.md</c>. Unload unregisters the Synced row.
    ///
    /// <para>
    /// <paramref name="acceptableValues"/> attaches an <c>AcceptableValueRange</c> /
    /// <c>AcceptableValueList</c>, which BepInEx writes into the cfg comment and the settings
    /// panel prints on the row. Bounds are <b>local metadata</b>: they are not on the wire, and a
    /// synced value that falls outside them is clamped on the client with a log line rather than
    /// applied out of range (<see cref="Progenitor.Config.SyncedBounds"/>). Pass a full
    /// <see cref="ConfigDescription"/> through the other overload if you built one already.
    /// </para>
    /// </summary>
    public ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        string? description = null,
        bool synced = false,
        AcceptableValueBase? acceptableValues = null)
    {
        if (acceptableValues == null)
        {
            EnsureAttached();
            if (synced) ConfigSync.ThrowIfNotSyncable(typeof(T));
            var plain = Config.Bind(section, key, defaultValue, description, synced: false);
            if (synced)
                Track(ConfigSync.Register(Guid, Version, plain));
            return plain;
        }

        return Bind(section, key, defaultValue, new ConfigDescription(description ?? "", acceptableValues), synced);
    }

    /// <summary>
    /// <see cref="Bind{T}(string,string,T,string,bool,AcceptableValueBase)"/> taking a
    /// <see cref="ConfigDescription"/> you built yourself. Synced entries are supported: the
    /// description (and therefore the acceptable values) stays local to each side.
    /// </summary>
    public ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        ConfigDescription description,
        bool synced = false)
    {
        EnsureAttached();
        if (description == null) throw new ArgumentNullException(nameof(description));
        if (synced) ConfigSync.ThrowIfNotSyncable(typeof(T));
        var entry = Config.Bind(section, key, defaultValue, description, synced: false);
        if (synced)
            Track(ConfigSync.Register(Guid, Version, entry));
        return entry;
    }

    /// <summary>Event-bus subscriptions recorded for undo.</summary>
    public PluginEvents Events { get { EnsureAttached(); return _events!; } }

    /// <summary>ECS system hooks recorded for undo.</summary>
    public PluginHooks Hooks { get { EnsureAttached(); return _hooks!; } }

    /// <summary>Chat-command registrations recorded for undo.</summary>
    public PluginCommands Commands { get { EnsureAttached(); return _commands!; } }

    /// <summary>
    /// Cloned-item / cloned-buff / cloned-recipe definitions and ordered prefab editors owned by this plugin.
    /// <see cref="PluginContent.DefineItem"/> / <see cref="PluginContent.DefineBuff"/> /
    /// <see cref="PluginContent.DefineRecipe"/> are the same fluent surfaces as
    /// <see cref="PrefabRegistry.DefineItem"/> / <see cref="PrefabRegistry.DefineBuff"/> /
    /// <see cref="RecipeDefinition.Define"/> except Register takes no owner (this plugin is
    /// the owner). <see cref="PluginContent.Edit"/> is the same as
    /// <see cref="PrefabRegistry.Edit"/> with this plugin as owner. Handles are reversed on unload.
    /// </summary>
    public PluginContent Content { get { EnsureAttached(); return _content!; } }

    /// <summary>
    /// Per-save key-value bag for <see cref="Guid"/>. Server: JSON next to the world save. Client:
    /// the same no-op store <see cref="ModSave.Store"/> returns, so shared plugin code does not
    /// need a side check.
    /// </summary>
    public IModStore Save { get { EnsureAttached(); return _save!; } }

    /// <summary>
    /// Server-level key-value bag for <see cref="Guid"/> that <b>survives wipes and world
    /// restores</b>: <c>&lt;persistentDataPath&gt;\progenitor-server\&lt;guid&gt;.json</c>, outside
    /// every world folder. For data meant to outlive a world (season stats, a hall of fame, bans,
    /// rewards carried across a wipe); world state belongs in <see cref="Save"/>. Same
    /// <see cref="IModStore"/> contract. Client: the no-op store. Inside a subclass of this type
    /// the property hides the static <c>Progenitor.Persistence.ServerSave</c> class; qualify it
    /// there. See <c>docs/modules.md</c> "Where should my data go?".
    /// </summary>
    public IModStore ServerSave { get { EnsureAttached(); return _serverSave!; } }

    /// <summary>Custom-event send/receive; receive handlers are dropped by handler identity on undo.</summary>
    public PluginNet Net { get { EnsureAttached(); return _net!; } }

    /// <summary>Mod-version handshake entries recorded for undo.</summary>
    public PluginHandshake Handshake { get { EnsureAttached(); return _handshake!; } }

    private PluginFeatures? _features;
    private PluginPeer? _peer;
    private PluginPeers? _peers;
    private PluginMessages? _messages;

    /// <summary>Features this mod offers to the other side (mod compatibility C1). Tracked: unload withdraws them.</summary>
    public PluginFeatures Features { get { EnsureAttached(); return _features ??= new PluginFeatures(this); } }

    /// <summary>Client: the joined server's features and mod versions. On the server <c>Known</c> is always false.</summary>
    public PluginPeer Peer { get { EnsureAttached(); return _peer ??= new PluginPeer(this); } }

    /// <summary>Server: each connected client's features and mod versions (<c>Peers.Of(user)</c>).</summary>
    public PluginPeers Peers { get { EnsureAttached(); return _peers ??= new PluginPeers(this); } }

    /// <summary>Versioned, tolerant messages (mod compatibility C2). Handlers are tracked: unload removes them.</summary>
    public PluginMessages Messages { get { EnsureAttached(); return _messages ??= new PluginMessages(this); } }

    private PluginRequests? _requests;
    private PluginSubscriptions? _subscriptions;

    /// <summary>Typed request and reply, larger than one packet when needed (docs/modules.md, "Requests, large messages and subscriptions"). Tracked.</summary>
    public PluginRequests Requests { get { EnsureAttached(); return _requests ??= new PluginRequests(this); } }

    /// <summary>Snapshot then deltas of keyed server state for an open panel. Tracked: unload ends every subscription and topic.</summary>
    public PluginSubscriptions Subscriptions { get { EnsureAttached(); return _subscriptions ??= new PluginSubscriptions(this); } }

    /// <summary>
    /// Named virtual systems recorded for undo. These are callbacks on
    /// <see cref="SystemHooks"/>, not injected <c>SystemBase</c> types — that is what makes
    /// them safe to reverse on reload.
    /// </summary>
    public PluginSystems Systems { get { EnsureAttached(); return _systems!; } }

    /// <summary>
    /// Castle plot / tile / building reads bound to this plugin's side world. Server is the
    /// intended side (territories live there); a client world returns whatever that world has.
    /// Read-only, so unload has nothing to reverse.
    /// </summary>
    public PluginCastles Castles { get { EnsureAttached(); return _castles!; } }

    /// <summary>
    /// Server chat send bound to this plugin's world. Fire-and-forget: unload has nothing to
    /// reverse. Client-side calls return 0 / false.
    /// </summary>
    public PluginChat Chat { get { EnsureAttached(); return _chat!; } }

    /// <summary>
    /// Server player directory (<see cref="Progenitor.Players.PlayerDirectory"/>): online
    /// enumeration and lookup by account id or display name. Ambiguous names return candidates.
    /// Session entity refs stay valid only while <see cref="PluginPlayers.IsFresh"/> is true.
    /// Read-only; unload has nothing to reverse.
    /// </summary>
    public PluginPlayers Players
    {
        get
        {
            EnsureAttached();
            return _players ??= new PluginPlayers(this);
        }
    }

    /// <summary>
    /// Server area triggers (<see cref="PluginTriggers"/>): run an action when a connected player
    /// enters or leaves a finite-height circle or axis-aligned rectangle, for example
    /// <c>ctx.Units.Spawn</c>. The first time a player is seen, the watch only records which side
    /// they are on. Client calls return an inactive handle. Each watch is <see cref="Track"/>ed,
    /// so unload stops it.
    /// </summary>
    public PluginTriggers Triggers
    {
        get
        {
            EnsureAttached();
            return _triggers ??= new PluginTriggers(this);
        }
    }

    /// <summary>
    /// Client-side vanilla-asset borrowing (<see cref="Assets"/>) and server-authored
    /// networked beams (<see cref="ServerBeam"/>). Client Borrow returns disabled handles;
    /// server <c>ServerBeam</c> / <c>Beams</c> spawn the coil entity. Each handle is
    /// <see cref="Track"/>ed so unload releases the plugin's effects root and beam entities.
    /// </summary>
    public PluginEffects Effects
    {
        get
        {
            EnsureAttached();
            return _effects ??= new PluginEffects(this);
        }
    }

    /// <summary>
    /// Client camera for cinematic shots (<see cref="Progenitor.Cameras.PluginCamera"/>): mode
    /// switch, orbit and free shots written every frame, reset. Server calls throw. Unload (every
    /// reason) resets the camera to top-down if this plugin still owns it; the reset is
    /// <see cref="Track"/>ed when the property is first read on the client.
    /// </summary>
    public Progenitor.Cameras.PluginCamera Camera
    {
        get
        {
            EnsureAttached();
            return _camera ??= new Progenitor.Cameras.PluginCamera(this);
        }
    }

    private Progenitor.Cameras.PluginCamera? _camera;

    /// <summary>
    /// Client presentation prefabs (<see cref="Progenitor.Models.PluginModels"/>): stream in a
    /// game model that is not loaded (for example <c>HYB_NPCDracula_Evolved</c>), hold it, and make
    /// inert owned copies. Server loads fail with <c>server-side</c>. Every handle is
    /// <see cref="Track"/>ed, so unload destroys the copies and releases the holds.
    /// </summary>
    public Progenitor.Models.PluginModels Models
    {
        get
        {
            EnsureAttached();
            return _models ??= new Progenitor.Models.PluginModels(this);
        }
    }

    private Progenitor.Models.PluginModels? _models;

    /// <summary>
    /// Client film cameras and lock-step capture (<see cref="Progenitor.Film.PluginFilm"/>). Server
    /// calls throw. Unload stops captures (the file so far stays valid) and releases cameras.
    /// </summary>
    public Progenitor.Film.PluginFilm Film
    {
        get
        {
            EnsureAttached();
            return _film ??= new Progenitor.Film.PluginFilm(this);
        }
    }

    private Progenitor.Film.PluginFilm? _film;

    /// <summary>
    /// The local player's input (<see cref="Progenitor.Inputs.PluginInput"/>): leases that aim,
    /// hover, walk and press buttons for the local character, one owner per channel. Client only
    /// (a server acquire returns <c>ServerSide</c>). Every lease this plugin holds ends on unload,
    /// for every reason.
    /// </summary>
    public Progenitor.Inputs.PluginInput Input
    {
        get
        {
            EnsureAttached();
            return _input ??= new Progenitor.Inputs.PluginInput(this);
        }
    }

    private Progenitor.Inputs.PluginInput? _input;

    /// <summary>
    /// Server-side unit spawn and steering (<see cref="Units"/>). Client calls return disabled
    /// handles so shared plugin code does not need a side check. Each spawn is
    /// <see cref="Track"/>ed so unload despawns what this plugin created.
    /// </summary>
    public PluginUnits Units
    {
        get
        {
            EnsureAttached();
            return _units ??= new PluginUnits(this);
        }
    }



    /// <summary>
    /// Server inventory grant and equip (<see cref="Progenitor.Items.Items"/>; client calls throw),
    /// and the item catalog (<c>Catalog()</c>, both sides). Grant defaults stack, accept a partial add, do not equip, and leave durability alone.
    /// </summary>
    public PluginItems Items
    {
        get
        {
            EnsureAttached();
            return _items ??= new PluginItems(this);
        }
    }

    /// <summary>Server recipe stations (<see cref="Progenitor.Stations.Stations"/>): recipes, the per-recipe switch, progress. Client calls throw.</summary>
    public Progenitor.Stations.PluginStations Stations { get { EnsureAttached(); return _stations ??= new Progenitor.Stations.PluginStations(this); } }
    private Progenitor.Stations.PluginStations? _stations;

    private Progenitor.Inventory.PluginInventory? _inventory;
    private Progenitor.Clans.PluginClans? _clans;

    /// <summary>Server inventories: read, add, remove, a total-conserving transfer, watches, stable container ids. Client calls throw.</summary>
    public Progenitor.Inventory.PluginInventory Inventory
    {
        get { EnsureAttached(); return _inventory ??= new Progenitor.Inventory.PluginInventory(this); }
    }

    private Progenitor.Materials.PluginMaterialSource? _materialSource;

    /// <summary>
    /// Build and craft material sources (P3, experimental): register the one provider that decides
    /// which containers pay, and read what is actually armed (<c>Status</c>). Register and
    /// invalidate are server only.
    /// </summary>
    public Progenitor.Materials.PluginMaterialSource MaterialSource
    {
        get { EnsureAttached(); return _materialSource ??= new Progenitor.Materials.PluginMaterialSource(this); }
    }

    /// <summary>Server clans: a user's clan, its members and castles. Client calls throw.</summary>
    public Progenitor.Clans.PluginClans Clans
    {
        get { EnsureAttached(); return _clans ??= new Progenitor.Clans.PluginClans(this); }
    }

    /// <summary>Server castle servants and hunts: the throne list, the hunt record, sends, resends, the list provider (experimental). Client calls throw.</summary>
    public Progenitor.Servants.PluginServants Servants { get { EnsureAttached(); return _servants ??= new Progenitor.Servants.PluginServants(this); } }
    private Progenitor.Servants.PluginServants? _servants;

    /// <summary>
    /// Server ability control (<see cref="Progenitor.Abilities.PluginAbilities"/>): force-cast a
    /// group the character need not have on its bar, and hold a character's cooldowns at zero.
    /// Holds are <see cref="Track"/>ed, so unload releases them. Client calls throw.
    /// </summary>
    public Progenitor.Abilities.PluginAbilities Abilities
    {
        get
        {
            EnsureAttached();
            return _abilities ??= new Progenitor.Abilities.PluginAbilities(this);
        }
    }

    private Progenitor.Abilities.PluginAbilities? _abilities;

    /// <summary>
    /// The world's time of day, server-side (<see cref="Progenitor.Clock.PluginTime"/>): read it,
    /// move it (<c>SetTimeOfDay</c>) or freeze it (<c>HoldTimeOfDay</c>). Connected clients' lighting
    /// follows. Also the world speed: <c>Freeze</c> and <c>SlowMotion</c> stop or slow the whole
    /// simulation on the server and every connected client. Overrides and leases are
    /// <see cref="Track"/>ed, so unload releases them and the world's own time and speed return.
    /// Client calls throw.
    /// </summary>
    public Progenitor.Clock.PluginTime Time
    {
        get
        {
            EnsureAttached();
            return _time ??= new Progenitor.Clock.PluginTime(this);
        }
    }

    private Progenitor.Clock.PluginTime? _time;

    /// <summary>
    /// Server boss AI (<see cref="Progenitor.Ai.PluginAi"/>): describe and observe, cast-option
    /// edits, health gates, phase thresholds, private behavior-tree copies, factions, and named
    /// variants that survive a world load. Every mutation returns a handle that restores it and is
    /// <see cref="Track"/>ed. Client calls throw. See <c>docs/ai.md</c>.
    /// </summary>
    public Progenitor.Ai.PluginAi Ai
    {
        get
        {
            EnsureAttached();
            return _ai ??= new Progenitor.Ai.PluginAi(this);
        }
    }

    private Progenitor.Ai.PluginAi? _ai;

    /// <summary>
    /// Server debug protection: invulnerable, ignored by foes, <c>Immortal</c>, and the kill-path
    /// guard. Client calls throw.
    /// </summary>
    public PluginDebug Debug
    {
        get
        {
            EnsureAttached();
            return _debug ??= new PluginDebug(this);
        }
    }

    /// <summary>
    /// Server-side timed buff application (<see cref="Progenitor.Buffs.Buffs"/>) for buffs this
    /// plugin defined with <see cref="PluginContent.DefineBuff"/>. A client call is refused with an
    /// actionable reason rather than writing anything. Each application is <see cref="Track"/>ed so
    /// unload removes the buffs this plugin applied. See <c>docs/buffs.md</c>.
    /// </summary>
    public PluginBuffs Buffs
    {
        get
        {
            EnsureAttached();
            return _buffs ??= new PluginBuffs(this);
        }
    }

    /// <summary>
    /// <b>Opt out of hot reload for this plugin</b> (job FHARM7; was <c>ctx.Harmony</c>). A raw
    /// Harmony instance whose id is this plugin's GUID.
    ///
    /// <para>The tutorial's rule one is "never hand Harmony a method that lives in your plugin",
    /// and this property is the only way left to break it. A patch applied through it pins this
    /// plugin's collectible ALC <em>and</em> the Core generation that applied it for the life of
    /// the process — the unpatch does not undo it, because
    /// <c>StackTraceFixes.RealMethodMap</c> and (for IL2CPP)
    /// <c>Il2CppDetourMethodPatcher.DelegateCache</c> are append-only.</para>
    ///
    /// <para>So <b>reading this property</b> — not patching through it, reading it — logs
    /// <see cref="Progenitor.Patches.UnsafeHarmonyUse.FormatFirstUse"/> once for this plugin and
    /// marks it non-hot-swappable: from then on the reloader refuses to swap it in place and
    /// asks for a restart. That is not a punishment, it is the truth about the pin; a plugin
    /// that wants hot reload uses <see cref="Patches"/>, which routes every body through
    /// Bootstrap's process-lifetime slot pool and hands HarmonyX nothing of yours.</para>
    ///
    /// <para>Unload still calls <see cref="Harmony.UnpatchSelf"/>, which stops the detour from
    /// calling into a dead ALC. It does not release the ALC.</para>
    /// </summary>
    public Harmony UnsafeHarmony
    {
        get
        {
            EnsureAttached();
            if (Progenitor.Patches.UnsafeHarmonyUse.Note(Guid))
            {
                Plugin.Logger?.LogWarning(Progenitor.Patches.UnsafeHarmonyUse.FormatFirstUse(Guid));
                _log?.LogWarning(Progenitor.Patches.UnsafeHarmonyUse.FormatFirstUse(Guid));
            }
            return _harmony!;
        }
    }

    /// <summary>
    /// Job FHARM: tagged Harmony patches with an unload-safe unpatch. Prefer this over
    /// <see cref="Harmony"/> for game (IL2CPP) targets — the registry resolves the site through
    /// the address library first, refuses the shared empty-body thunk and Burst jobs, logs one
    /// line per patch and per unpatch, fails closed when an apply throws, and
    /// <c>PluginReloader.SweepRegistrations</c> unpatches everything tagged with this plugin on
    /// every unload reason (the plugin assembly is going away in all of them, and a patch whose
    /// method lives in a dead ALC is a crash, not a convenience).
    /// </summary>
    public Progenitor.Patches.PluginPatches Patches
    {
        get { EnsureAttached(); return new Progenitor.Patches.PluginPatches(Owner, Guid); }
    }

    /// <summary>
    /// Job FHARM: mark entities the game's unguarded kill paths may not kill. See
    /// <see cref="Progenitor.GodMode.GodMode"/> for which paths those are and why no component
    /// covers them. Entries are tagged with this plugin and dropped on unload.
    /// </summary>
    public Progenitor.GodMode.PluginGodMode GodMode
    {
        get { EnsureAttached(); return new Progenitor.GodMode.PluginGodMode(Owner); }
    }

    /// <summary>
    /// Job FEPOCH: the world-load epoch. Stamp <c>ctx.WorldState.LoadEpoch</c> next to any
    /// <c>Entity</c> you keep past the current frame and drop the id when
    /// <c>ctx.WorldState.IsStale(stamped)</c> — a V Rising save reload rebuilds every entity from
    /// index 0 at version 1, so <c>EntityManager.Exists</c> on a retained <c>index:version</c>
    /// pair can answer true while naming somebody else's entity (R32 findings 1 and 6). Named
    /// <c>WorldState</c> because <see cref="World"/> is already this plugin's ECS world.
    /// </summary>
    public Progenitor.WorldState.PluginWorldState WorldState
    {
        get { EnsureAttached(); return Progenitor.WorldState.PluginWorldState.Instance; }
    }

    /// <summary>
    /// Job FUI5: client keybinds backed by a <c>[Keybinds]</c> string entry in this plugin's
    /// <c>GUID.cfg</c>, polled once a frame and listed (with a rebind flow) in
    /// <see cref="Progenitor.Ui.SettingsPanel"/>. Touching this property also registers this
    /// plugin's config file as a settings source, so its own settings appear in that panel.
    /// Registrations are <see cref="Track"/>ed, so unload unregisters them. Safe on the server:
    /// the bind registers and logs but never fires (there is no keyboard).
    /// </summary>
    public Progenitor.Ui.PluginKeybinds Keybinds
    {
        get
        {
            EnsureAttached();
            return _keybinds ??= new Progenitor.Ui.PluginKeybinds(this);
        }
    }

    private Progenitor.Ui.PluginKeybinds? _keybinds;

    /// <summary>Process this context is running in. Never <see cref="Side.Both"/> — that flag is only a declaration.</summary>
    public Side Side { get { EnsureAttached(); return _side; } }

    /// <summary>True when <see cref="Side"/> is <see cref="Side.Server"/>.</summary>
    public bool IsServer => Side == Side.Server;

    /// <summary>True when <see cref="Side"/> is <see cref="Side.Client"/>.</summary>
    public bool IsClient => Side == Side.Client;

    /// <summary>The side's main ECS world, or null if it has not been created yet.</summary>
    public World? World
    {
        get
        {
            EnsureAttached();
            return _side == Side.Server ? Worlds.Server : Worlds.Client;
        }
    }

    /// <summary>BepInEx plugin GUID. Stable id for handshake entries and Harmony.</summary>
    public string Guid { get; private set; } = "";

    /// <summary>BepInEx plugin name, for logs.</summary>
    public string Name { get; private set; } = "";

    /// <summary>BepInEx plugin version string, for handshake <c>Require</c>/<c>Provide</c>.</summary>
    public string Version { get; private set; } = "";

    /// <summary>
    /// Object used as the owner key for every registration this context records (the BepInEx
    /// plugin instance). Public so a command class that holds this context can pass it to APIs
    /// that still take an explicit owner.
    /// </summary>
    public object Owner { get; private set; } = null!;

    internal void Attach(BasePlugin plugin, Side side)
    {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (_attached) throw new InvalidOperationException("PluginContext is already attached.");

        var meta = MetadataHelper.GetMetadata(plugin);
        _plugin = plugin;
        _log = plugin.Log;
        _assembly = plugin.GetType().Assembly;
        Owner = plugin;
        _side = side;
        Guid = meta.GUID;
        Name = meta.Name;
        Version = meta.Version.ToString();

        _harmony = new Harmony(Guid);
        _config = new PluginConfig(plugin.Config);
        _events = new PluginEvents(this);
        _hooks = new PluginHooks(this);
        _commands = new PluginCommands(this, _assembly);
        _content = new PluginContent(this);
        _net = new PluginNet(this);
        _handshake = new PluginHandshake(this);
        _systems = new PluginSystems(this);
        _castles = new PluginCastles(this);
        _chat = new PluginChat(this);
        _save = ModSave.Store(Guid);
        _serverSave = Progenitor.Persistence.ServerSave.Store(Guid);
        _attached = true;

        // First in the bag so reverse-order undo unpatches Harmony last, after handlers are gone.
        Track(new ActionDisposable(() => _harmony!.UnpatchSelf()));
        Track(new ActionDisposable(() => VirtualSystems.RemoveAll(Owner)));

        // Job FCFG13: this plugin's ConfigFile leaves ConfigHelper's per-path reload registry when
        // the context is torn down — every unload reason, and a failed OnLoad too, because that
        // path runs the same UndoAll. ConfigHelper.Bind and ConfigHelper.Watch enroll whatever file
        // they are handed (ctx.Config.File is what a plugin hands them), and until now the only way
        // out was the plugin calling DropWatchedFile itself, which is the piece FCFG12 named as
        // missing: an instance nobody dropped stayed registered until the GC took it, and every
        // watch event in between reloaded it and ran the SettingChanged handlers it still carried.
        // The handle guards its own drop and is a no-op when the plugin already dropped by hand.
        Track(ConfigHelper.DropWatchedFileOnUnload(_config!.File));
    }

    /// <summary>
    /// Record a disposable so unload (or a failed <c>OnLoad</c>) disposes it. Use this for
    /// anything the other helpers do not wrap: extra Harmony ids, native detours, file handles.
    /// </summary>
    public void Track(IDisposable disposable)
    {
        if (disposable == null) throw new ArgumentNullException(nameof(disposable));
        _bag.Add(disposable);
    }

    /// <summary>Forget a tracked disposable that was already disposed elsewhere. False when it was not tracked.</summary>
    internal bool Untrack(IDisposable disposable) => _bag.Remove(disposable);

    internal void UndoAll() => _bag.UndoAll(_log ?? _plugin?.Log ?? Plugin.Logger);

    /// <summary>
    /// PINS1: true once unload starts reversing this context's registrations, which is after the plugin's
    /// <c>OnUnload</c> has returned (calls made inside <c>OnUnload</c> still work and are swept). Facades that would
    /// register a plugin callback in a Core table (a request, a subscription, a notice, a tip) refuse
    /// once this is set: the sweep for this plugin has run or is running, so a registration made now
    /// would never be removed and would pin the plugin's load context.
    /// </summary>
    public bool IsRetired => _bag.IsUndone;

    internal void EnsureAttached()
    {
        if (!_attached)
            throw new InvalidOperationException("PluginContext is not attached. Put setup in OnLoad, not the context constructor.");
    }
}

/// <summary>
/// Thin bind wrapper over BepInEx <see cref="ConfigFile"/>. Exists so a mod can take
/// <see cref="PluginContext"/> as its only surface and not import BepInEx.Configuration for the
/// common case.
/// </summary>
public sealed class PluginConfig
{
    internal PluginConfig(ConfigFile file) => File = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>The underlying BepInEx file, for Reload/Save or binds that need a <see cref="ConfigDescription"/>.</summary>
    public ConfigFile File { get; }

    /// <summary>
    /// Bind a setting in this plugin's <c>GUID.cfg</c>. Same key returns the existing entry.
    /// Refuses Bootstrap's shared <c>progenitor.cfg</c>: that file is process-lifetime
    /// and must only hold BCL primitives/string/BCL enums bound through <see cref="ConfigHelper"/>.
    /// When <paramref name="synced"/> is true this method throws: the handle cannot be
    /// tracked here. Use <see cref="PluginContext.Bind{T}"/> so unload unregisters.
    /// </summary>
    public ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        string? description = null,
        bool synced = false)
    {
        Guard(section, key);

        if (synced)
            ConfigSync.ThrowIfUntrackedSyncedBind();

        return description == null
            ? File.Bind(section, key, defaultValue)
            : File.Bind(section, key, defaultValue, description);
    }

    /// <summary>
    /// Same as <see cref="Bind{T}(string,string,T,string,bool)"/> with a full
    /// <see cref="ConfigDescription"/>, so a range or a list can ride along. Prefer
    /// <see cref="PluginContext.Bind{T}(string,string,T,ConfigDescription,bool)"/>, which also
    /// handles <paramref name="synced"/>.
    /// </summary>
    public ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        ConfigDescription description,
        bool synced = false)
    {
        if (description == null) throw new ArgumentNullException(nameof(description));
        Guard(section, key);
        if (synced)
            ConfigSync.ThrowIfUntrackedSyncedBind();
        return File.Bind(section, key, defaultValue, description);
    }

    private void Guard(string section, string key)
    {
        if (string.IsNullOrEmpty(section)) throw new ArgumentException("Section is required.", nameof(section));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (ConfigHelper.IsSharedConfigFile(File))
        {
            throw new InvalidOperationException(
                "PluginContext.Config.Bind cannot use the shared Progenitor ConfigFile ("
                + ConfigHelper.SharedFileName
                + "). That file is process-lifetime; ConfigEntry<T> of a Core or plugin type pins "
                + "a collectible generation. Bind plugin settings on this plugin's own GUID.cfg.");
        }
    }
}

/// <summary>Event-bus facade that records each subscription on the owning context.</summary>
public sealed class PluginEvents
{
    private readonly PluginContext _ctx;
    internal PluginEvents(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Subscribe on the framework bus. Higher <paramref name="priority"/> runs first.
    /// Passes this plugin's GUID as <c>pluginId</c> so equal-priority order is stable
    /// across a reload (load-order slot is sticky per guid). Dispose (or plugin unload)
    /// drops the handler so a reload cannot stack copies.
    /// </summary>
    public IDisposable On<T>(Action<T> handler, int priority = 0) =>
        On(handler, priority, before: null, after: null);

    /// <summary>
    /// Subscribe with Harmony-style <paramref name="before"/> / <paramref name="after"/>
    /// plugin ids. Missing ids are ignored. This plugin's GUID is the <c>pluginId</c>
    /// others name in those arrays.
    /// </summary>
    public IDisposable On<T>(Action<T> handler, int priority, string[]? before, string[]? after)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = EventBus.On(
            handler,
            priority,
            owner: _ctx.Owner,
            before: before,
            after: after,
            pluginId: _ctx.Guid);
        _ctx.Track(sub);
        return sub;
    }
}

/// <summary>ECS system-hook facade that records each registration on the owning context.</summary>
public sealed class PluginHooks
{
    private readonly PluginContext _ctx;
    internal PluginHooks(PluginContext ctx) => _ctx = ctx;

    /// <summary>Run <paramref name="hook"/> before <typeparamref name="TSystem"/> updates. Dispose to remove it.</summary>
    public IDisposable Before<TSystem>(Action<SystemHookContext> hook, HookOptions? options = null)
        => Track(SystemHooks.Before<TSystem>(hook, WithPluginId(options)));

    /// <summary>Run <paramref name="hook"/> after <typeparamref name="TSystem"/> updates. Dispose to remove it.</summary>
    public IDisposable After<TSystem>(Action<SystemHookContext> hook, HookOptions? options = null)
        => Track(SystemHooks.After<TSystem>(hook, WithPluginId(options)));

    /// <summary>Non-generic Before. <paramref name="systemType"/> is a game SystemBase or ISystem type.</summary>
    public IDisposable Before(Type systemType, Action<SystemHookContext> hook, HookOptions? options = null)
        => Track(SystemHooks.Before(systemType, hook, WithPluginId(options)));

    /// <summary>Non-generic After. <paramref name="systemType"/> is a game SystemBase or ISystem type.</summary>
    public IDisposable After(Type systemType, Action<SystemHookContext> hook, HookOptions? options = null)
        => Track(SystemHooks.After(systemType, hook, WithPluginId(options)));

    /// <summary>
    /// Invoke <paramref name="callback"/> once when a world of this name exists. Fires immediately
    /// if it is already created. Dispose cancels a wait that has not fired yet.
    /// </summary>
    public IDisposable WhenWorldExists(string worldName, Action<World> callback)
        => Track(SystemHooks.WhenWorldExists(worldName, callback));

    private IDisposable Track(IDisposable registration)
    {
        _ctx.Track(registration);
        return registration;
    }

    /// <summary>
    /// Default <see cref="HookOptions.PluginId"/> to this plugin's GUID so equal-priority
    /// hooks stay stable across a reload. Does not mutate the caller's instance.
    /// </summary>
    private HookOptions WithPluginId(HookOptions? options)
    {
        if (options == null)
            return new HookOptions { PluginId = _ctx.Guid };
        if (!string.IsNullOrEmpty(options.PluginId))
            return options;
        return new HookOptions
        {
            OnlyWhenSystemRuns = options.OnlyWhenSystemRuns,
            EveryNFrames = options.EveryNFrames,
            Priority = options.Priority,
            Before = options.Before,
            After = options.After,
            PluginId = _ctx.Guid,
        };
    }
}

/// <summary>Chat-command facade that records each registration on the owning context.</summary>
public sealed class PluginCommands
{
    private readonly PluginContext _ctx;
    private readonly Assembly _assembly;

    internal PluginCommands(PluginContext ctx, Assembly assembly)
    {
        _ctx = ctx;
        _assembly = assembly;
    }

    /// <summary>
    /// Discover <c>[Command]</c> methods on <typeparamref name="T"/> and register them under this
    /// plugin. <typeparamref name="T"/> may be an instance class (parameterless constructor shared
    /// by instance methods) or a type that only has static command methods (invoked with a null
    /// target). A C# <c>static</c> class cannot be a generic type argument — use
    /// <see cref="Register(Type)"/> for those. Dispose (or unload) drops the commands so a reload
    /// replaces rather than stacks.
    /// </summary>
    public IDisposable Register<T>() => Register(typeof(T));

    /// <summary>
    /// Non-generic <see cref="Register{T}"/>. Accepts instance classes and C# static classes;
    /// static methods are invoked with a null target, instance methods with a constructed instance.
    /// </summary>
    public IDisposable Register(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        var handle = CommandRegistry.RegisterType(type, _ctx.Owner);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Discover <c>[Command]</c> methods on <paramref name="instance"/>'s type and invoke instance
    /// methods on this object so the command class can hold a <see cref="PluginContext"/>. Static
    /// methods on the same type are still invoked with a null target.
    /// </summary>
    public IDisposable Register(object instance)
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        var handle = CommandRegistry.RegisterInstance(instance, _ctx.Owner);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Discover every <c>[Command]</c> method in this plugin's assembly. Pass an assembly only
    /// when commands live in a referenced DLL rather than next to the plugin class.
    /// </summary>
    public IDisposable RegisterAssembly() => RegisterAssembly(_assembly);

    /// <summary>Discover <c>[Command]</c> methods in <paramref name="assembly"/> under this plugin.</summary>
    public IDisposable RegisterAssembly(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        var handle = CommandRegistry.RegisterAssembly(assembly, _ctx.Owner);
        _ctx.Track(handle);
        return handle;
    }
}

/// <summary>
/// Custom-event facade. Receive registrations remember the handler instance so unload removes
/// only that handler; send methods are pass-throughs to <see cref="CustomEvents"/>.
/// </summary>
public sealed class PluginNet
{
    private readonly PluginContext _ctx;
    internal PluginNet(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// The reserved-range rule on the way out (job FEX4, R57 finding 3). <see cref="CustomEvents"/>
    /// attributes a send to its immediate caller, and that caller is this class — Core code — so a
    /// plugin's send through the facade would read as Core's. Here the sender is a plugin by
    /// construction, so the check is made with the plugin's own assembly name (its GUID when even
    /// that cannot be read). One error line per id per Core generation; the send returns
    /// false / 0.
    ///
    /// <para>The name is for the log line only (job FEX6, R58 finding 12).
    /// <see cref="CustomEvents.SendAllowedFor"/> treats this path as never-Core whatever the name
    /// says, so a plugin assembly called <c>Progenitor</c> gets no exemption — it used to get one,
    /// because Core-ness was a string comparison against <c>"Progenitor"</c>.</para>
    /// </summary>
    private bool MaySend(int eventId)
    {
        NoteRawSend(eventId);
        if (!CustomEvents.IsReservedEventId(eventId)) return true;
        string owner;
        try { owner = _ctx.Owner?.GetType().Assembly.GetName().Name ?? _ctx.Guid; }
        catch { owner = _ctx.Guid; }
        return CustomEvents.SendAllowedFor(eventId, owner);
    }

    /// <summary>
    /// Client: handle a server-to-client event. Unload removes this handler if it still owns
    /// the event id, leaving a later registrant of the same id in place — through the
    /// owner-checked removal path, so a plugin's teardown can only ever drop its own rows
    /// (job FEX6, R58 finding 1).
    ///
    /// <para>This is the surface to prefer for a handler you may want to remove. Removal is
    /// attributed to the <em>calling</em> assembly (job FEX8, R59 findings 2 and 7), and the
    /// disposable below calls from Core, so it works whichever assembly declared the handler —
    /// a helper library you ship, or a delegate nothing can name. Calling
    /// <c>CustomEvents.RemoveHandler</c> yourself is attributed to you.</para>
    /// </summary>
    public IDisposable OnReceive(int eventId, CustomEventHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        CustomEvents.OnReceive(eventId, handler);
        var d = new ActionDisposable(() => CustomEvents.RemoveHandler(eventId, handler));
        _ctx.Track(d);
        return d;
    }

    /// <summary>
    /// Server: handle a client-to-server event. Unload removes this handler if it still owns
    /// the event id, leaving a later registrant of the same id in place — through the
    /// owner-checked removal path (job FEX6, R58 finding 1), and from Core, so the caller-attributed
    /// rule of job FEX8 (R59 findings 2 and 7) does not stand between a plugin and its own handle.
    /// </summary>
    public IDisposable OnReceiveFromClient(int eventId, CustomEventFromClientHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        CustomEvents.OnReceiveFromClient(eventId, handler);
        var d = new ActionDisposable(() => CustomEvents.RemoveClientHandler(eventId, handler));
        _ctx.Track(d);
        return d;
    }

    /// <summary>
    /// Server: send an unmanaged payload to one user by entity. Returns false if
    /// <paramref name="eventId"/> is in Progenitor's reserved <c>0x5072____</c> range.
    /// </summary>
    public bool SendToUser<T>(World server, Entity userEntity, int eventId, in T value) where T : unmanaged
        => MaySend(eventId) && CustomEvents.SendToUser(server, userEntity, eventId, value);

    /// <summary>Server: send using this context's world. Returns false if the world is not ready.</summary>
    public bool SendToUser<T>(Entity userEntity, int eventId, in T value) where T : unmanaged
    {
        if (!MaySend(eventId)) return false;
        var world = _ctx.World;
        return world != null && CustomEvents.SendToUser(world, userEntity, eventId, value);
    }

    /// <summary>
    /// Server: send an unmanaged payload to every connected user. Returns 0 if
    /// <paramref name="eventId"/> is in Progenitor's reserved <c>0x5072____</c> range.
    /// </summary>
    public int SendToAll<T>(World server, int eventId, in T value) where T : unmanaged
        => MaySend(eventId) ? CustomEvents.SendToAll(server, eventId, value) : 0;

    /// <summary>Server: send to every connected user on this context's world. Returns 0 if the world is not ready.</summary>
    public int SendToAll<T>(int eventId, in T value) where T : unmanaged
    {
        if (!MaySend(eventId)) return 0;
        var world = _ctx.World;
        return world == null ? 0 : CustomEvents.SendToAll(world, eventId, value);
    }

    /// <summary>
    /// Client: send an unmanaged payload to the server. Returns false if
    /// <paramref name="eventId"/> is in Progenitor's reserved <c>0x5072____</c> range.
    /// </summary>
    public bool SendToServer<T>(int eventId, in T value) where T : unmanaged
        => MaySend(eventId) && CustomEvents.SendToServer(eventId, value);

    // -1 not yet read; 0 not applicable (an Exact rule: a peer running this mod runs this version); 1 check.
    private int _rawWarningApplies = -1;

    // Event ids already warned about (or failed): never checked again this context.
    private readonly HashSet<int> _rawSettled = new();

    /// <summary>
    /// Mod compatibility C2: the raw-message warning. One line per mod and id per Core generation when a peer runs this
    /// mod and the two may be on different versions (the mod declares no <see cref="NetworkCompatibilityAttribute"/> or
    /// a rule other than Exact, or the peer's version differs). Never blocks the send. Settled up front: a mod with an
    /// Exact rule skips the check entirely, and an id that was warned about costs one set lookup per send.
    /// </summary>
    private void NoteRawSend(int eventId)
    {
        if (_rawWarningApplies == 0 || CustomEvents.IsReservedEventId(eventId)) return;
        try
        {
            if (_rawWarningApplies < 0)
            {
                // Compat C3: only a build whose effective range is one exact version (an explicit Exact rule) skips it.
                var compat = CompatDeclaration.From(_ctx.Owner?.GetType(), _ctx.Version);
                _rawWarningApplies = compat != null && compat.EffectiveRange.StartsWith("=", StringComparison.Ordinal) ? 0 : 1;
                if (_rawWarningApplies == 0) return;
            }

            if (_rawSettled.Contains(eventId)) return;
            if (ModCompatNet.NoteRawSend(_ctx.Guid, _ctx.Version, true, eventId, -1)) _rawSettled.Add(eventId);
        }
        catch { _rawSettled.Add(eventId); /* a warning never costs a send */ }
    }
}

/// <summary>
/// Handshake facade. Each <see cref="Require"/> / <see cref="Provide"/> is undone by guid so a
/// reload does not leave a stale version the next join will kick for. <see cref="DeclareSelf"/>
/// reads <see cref="NetworkCompatibilityAttribute"/> when present so the level and version rule
/// go on the wire.
/// </summary>
public sealed class PluginHandshake
{
    private readonly PluginContext _ctx;
    private int _selfDeclared;

    internal PluginHandshake(PluginContext ctx) => _ctx = ctx;

    /// <summary>Server: clients must present this mod at this version (undeclared exact-match). Unload drops the requirement.</summary>
    public void Require(string guid, string version)
    {
        if (string.IsNullOrEmpty(guid)) throw new ArgumentException("GUID is required.", nameof(guid));
        if (version == null) throw new ArgumentNullException(nameof(version));
        Handshake.Require(guid, version);
        _ctx.Track(new ActionDisposable(() => Handshake.Unrequire(guid)));
    }

    /// <summary>
    /// Server: record this mod with an explicit compatibility level. Declared rows are evaluated
    /// in both directions. Unload drops the requirement.
    /// </summary>
    public void Require(string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule versionRule)
    {
        if (string.IsNullOrEmpty(guid)) throw new ArgumentException("GUID is required.", nameof(guid));
        if (version == null) throw new ArgumentNullException(nameof(version));
        Handshake.Require(guid, version, level, versionRule);
        _ctx.Track(new ActionDisposable(() => Handshake.Unrequire(guid)));
    }

    /// <summary>Client: announce this mod at this version (undeclared). Unload drops the announcement.</summary>
    public void Provide(string guid, string version)
    {
        if (string.IsNullOrEmpty(guid)) throw new ArgumentException("GUID is required.", nameof(guid));
        if (version == null) throw new ArgumentNullException(nameof(version));
        Handshake.ProvideOwned(_ctx.Guid, guid, version);
        _ctx.Track(new ActionDisposable(() => Handshake.Unprovide(guid)));
    }

    /// <summary>
    /// Client: announce this mod with an explicit compatibility level so the server can evaluate
    /// the reverse direction. Unload drops the announcement.
    /// </summary>
    public void Provide(string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule versionRule)
    {
        if (string.IsNullOrEmpty(guid)) throw new ArgumentException("GUID is required.", nameof(guid));
        if (version == null) throw new ArgumentNullException(nameof(version));
        Handshake.ProvideOwned(_ctx.Guid, guid, version, level, versionRule);
        _ctx.Track(new ActionDisposable(() => Handshake.Unprovide(guid)));
    }

    /// <summary>
    /// Client: whether this plugin's server part runs on the joined server, from that server's
    /// handshake request — the evidence the mod page's status bar and <c>needsServerPart</c> rows
    /// read (<see cref="CounterpartPresence"/>). Read it when you need it; it changes when a join
    /// starts or ends. Before sending the server part a message, also wait for the join to be
    /// accepted (<c>Handshake.LastClientResult == "ok"</c>): the server refuses mod packets from a
    /// user whose handshake has not passed. On the server this is always
    /// <see cref="CounterpartPresence.Running"/> (the plugin is the server part). Never throws.
    /// </summary>
    public CounterpartPresence ServerPart
    {
        get
        {
            if (_ctx.IsServer) return CounterpartPresence.Running;
            try { return CounterpartPolicy.ServerPartFrom(Handshake.CopyServerAsked(), _ctx.Guid, Handshake.ServerRequestTruncated); }
            catch { return CounterpartPresence.Unknown; }
        }
    }

    /// <summary>Server: require this plugin's own GUID and version (undeclared exact-match).</summary>
    public void RequireSelf() => Require(_ctx.Guid, _ctx.Version);

    /// <summary>Client: provide this plugin's own GUID and version (undeclared).</summary>
    public void ProvideSelf() => Provide(_ctx.Guid, _ctx.Version);

    /// <summary>
    /// Removed 2026-10-05: servers no longer distribute mod files; mods come only from Thunderstore (accountable
    /// authors, easy takedown). Kept so a plugin built against an older Core still loads: it does nothing but log one
    /// warning per mod id per Core generation, and returns a handle
    /// whose <c>Dispose</c> does nothing. Publish the mod on Thunderstore and name its package in the server's
    /// <c>progenitor-manifest.json</c> so players can install it from the join screens.
    /// </summary>
    [Obsolete("Servers no longer distribute mod files; publish the mod on Thunderstore. This call does nothing.")]
    public IDisposable OfferPackage(string path)
    {
#pragma warning disable CS0618 // the obsolete static is the same no-op
        return ModManifest.Offer(_ctx.Guid, path);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Removed 2026-10-05 with <see cref="OfferPackage"/>: always false, with <paramref name="reason"/>
    /// <c>"removed"</c>, an empty hash and size 0.
    /// </summary>
    [Obsolete("Servers no longer distribute mod files; publish the mod on Thunderstore. This call always answers false.")]
    public bool TryGetOfferedPackageState(out string sha256, out long size, out string reason)
    {
        sha256 = "";
        size = 0;
        reason = "removed";
        return false;
    }

    /// <summary>
    /// Record a disposable on the owning plugin so unload reverses it. Use for receive
    /// handlers and extra offers the other handshake helpers do not wrap.
    /// </summary>
    public IDisposable Track(IDisposable disposable)
    {
        _ctx.Track(disposable);
        return disposable;
    }

    /// <summary>
    /// Declare this plugin's GUID and version on whichever side this context is running.
    /// When the plugin type has <see cref="NetworkCompatibilityAttribute"/>, that level and
    /// version rule are used; otherwise this is the original undeclared exact-match
    /// (<see cref="RequireSelf"/> on the server, <see cref="ProvideSelf"/> on the client).
    /// Idempotent: a second call from the same context is a no-op so the attribute auto-apply
    /// after <c>OnLoad</c> does not double-track.
    /// </summary>
    public void DeclareSelf()
    {
        if (Interlocked.Exchange(ref _selfDeclared, 1) != 0) return;

        var attr = _ctx.Owner.GetType().GetCustomAttribute<NetworkCompatibilityAttribute>();
        if (attr != null)
        {
            // SETTINGS1 stage 3: a mod that declares it runs without its counterpart
            // ([Counterpart(Limited or Optional)]) cannot also demand it (docs/mod-settings.md).
            var counterpart = _ctx.Owner.GetType().GetCustomAttribute<CounterpartAttribute>();
            var level = CounterpartPolicy.EffectiveLevel(
                attr.Level, counterpart?.WithoutCounterpart, _ctx.IsServer, counterpart?.ClientExtras);
            if (level != attr.Level && counterpart != null)
                Progenitor.Plugin.Logger?.LogInfo(CounterpartPolicy.LoweredLog(_ctx.Guid, counterpart.WithoutCounterpart));
            if (_ctx.IsServer) Require(_ctx.Guid, _ctx.Version, level, attr.VersionRule);
            else Provide(_ctx.Guid, _ctx.Version, level, attr.VersionRule);
            return;
        }

        if (_ctx.IsServer) RequireSelf();
        else ProvideSelf();
    }
}

/// <summary>
/// Virtual-system facade that records each registration on the owning context. Default anchor
/// is After <c>SimulationSystemGroup</c> on this plugin's side world; dispose (or unload) drops
/// the callback so a reload cannot stack copies.
/// </summary>
public sealed class PluginSystems
{
    private readonly PluginContext _ctx;
    internal PluginSystems(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Run <paramref name="onUpdate"/> after simulation on this plugin's side. Lower
    /// <paramref name="order"/> runs first. Duplicate <paramref name="name"/> from this plugin
    /// replaces the previous registration.
    /// </summary>
    public IDisposable Add(string name, Action<VirtualSystemContext> onUpdate, int order = 0)
        => Add(name, VirtualSystemAnchor.AfterSimulation, onUpdate, order);

    /// <summary>
    /// Run <paramref name="onUpdate"/> at <paramref name="anchor"/>, restricted to this plugin's
    /// process side so a server plugin cannot accidentally run on <c>Client_0</c>. Tracked for undo.
    /// </summary>
    public IDisposable Add(string name, VirtualSystemAnchor anchor, Action<VirtualSystemContext> onUpdate, int order = 0)
    {
        if (onUpdate == null) throw new ArgumentNullException(nameof(onUpdate));
        var bound = BindSide(anchor);
        var handle = VirtualSystems.Add(name, bound, onUpdate, _ctx.Owner, order);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>Named After on a vanilla system, this plugin's side, tracked for undo.</summary>
    public IDisposable AddAfter<TSystem>(string name, Action<VirtualSystemContext> onUpdate, int order = 0)
        => Add(name, VirtualSystemAnchor.After<TSystem>(), onUpdate, order);

    /// <summary>Named Before on a vanilla system, this plugin's side, tracked for undo.</summary>
    public IDisposable AddBefore<TSystem>(string name, Action<VirtualSystemContext> onUpdate, int order = 0)
        => Add(name, VirtualSystemAnchor.Before<TSystem>(), onUpdate, order);

    /// <summary>Drop every virtual system this plugin registered. Unload also does this.</summary>
    public int RemoveAll() => VirtualSystems.RemoveAll(_ctx.Owner);

    /// <summary>Process-wide diagnostic string (same as <see cref="VirtualSystems.Describe"/>).</summary>
    public string Describe() => VirtualSystems.Describe();

    private VirtualSystemAnchor BindSide(VirtualSystemAnchor anchor)
    {
        var side = anchor.Side & _ctx.Side;
        if (side == 0) side = _ctx.Side;
        return anchor.WithSide(side);
    }
}

/// <summary>
/// Prefab-registry facade bound to one plugin. Exists so a mod can define items, buffs, recipes, and
/// ordered prefab editors through <see cref="PluginContext"/> and have unload reverse them
/// without passing an owner to Register or <see cref="PrefabRegistry.Edit"/>.
/// </summary>
public sealed class PluginContent
{
    private readonly PluginContext _ctx;
    internal PluginContent(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Starts a cloned-item definition. Same fluent surface as
    /// <see cref="PrefabRegistry.DefineItem"/>; <see cref="ItemDefinitionBuilder.Register"/> uses
    /// this plugin as owner and records the handle for unload.
    /// </summary>
    public ItemDefinitionBuilder DefineItem(string id) =>
        new ItemDefinitionBuilder(_ctx, PrefabRegistry.DefineItem(id));

    /// <summary>
    /// Starts a cloned-buff definition. Same fluent surface as
    /// <see cref="PrefabRegistry.DefineBuff"/>; <see cref="BuffDefinitionBuilder.Register"/> uses
    /// this plugin as owner and records the handle for unload. Source must have <c>Buff</c>.
    /// </summary>
    public BuffDefinitionBuilder DefineBuff(string id) =>
        new BuffDefinitionBuilder(_ctx, PrefabRegistry.DefineBuff(id));

    /// <summary>
    /// Starts a cloned-recipe definition. Same fluent surface as
    /// <see cref="RecipeDefinition.Define"/>; <see cref="RecipeDefinitionBuilder.Register()"/> uses
    /// this plugin as owner and records the handle for unload. Source must have <c>RecipeData</c>.
    /// Workstation kind only in this increment. See <c>docs/recipes.md</c>.
    /// </summary>
    public RecipeDefinitionBuilder DefineRecipe(string id)
    {
        var builder = RecipeDefinition.Define(id);
        builder.BindPlugin(_ctx.Owner, _ctx.Track);
        return builder;
    }

    /// <summary>
    /// Ordered editor for a cloned or vanilla prefab. Runs after the registry has
    /// cloned/registered that GUID (or the collection already has the vanilla entity) and
    /// before a definition packet is sent. Dispose / unload drops it. A before/after cycle
    /// refuses the new editor (Warn in the log, handle already invalid).
    /// </summary>
    public IDisposable Edit(
        PrefabGUID prefabGuid,
        Action<PrefabEditHandle> editor,
        PrefabEditPriority priority = PrefabEditPriority.Default,
        string[]? before = null,
        string[]? after = null)
    {
        var handle = PrefabRegistry.Edit(
            prefabGuid, editor, _ctx.Owner, priority, before, after, _ctx.Guid, _ctx.Name);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Same as <see cref="Edit(PrefabGUID, Action{PrefabEditHandle}, PrefabEditPriority, string[], string[])"/>
    /// with a raw priority int so a mod can pass <c>(int)Early + 5</c>.
    /// </summary>
    public IDisposable Edit(
        PrefabGUID prefabGuid,
        Action<PrefabEditHandle> editor,
        int priority,
        string[]? before = null,
        string[]? after = null)
    {
        var handle = PrefabRegistry.Edit(
            prefabGuid, editor, _ctx.Owner, priority, before, after, _ctx.Guid, _ctx.Name);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Fluent clone-item builder bound to a <see cref="PluginContext"/>. Mirrors
    /// <see cref="PrefabRegistry.ItemDefinitionBuilder"/>; the only difference is
    /// <see cref="Register"/> takes no owner.
    /// </summary>
    public sealed class ItemDefinitionBuilder
    {
        private readonly PluginContext _ctx;
        private readonly PrefabRegistry.ItemDefinitionBuilder _inner;

        internal ItemDefinitionBuilder(PluginContext ctx, PrefabRegistry.ItemDefinitionBuilder inner)
        {
            _ctx = ctx;
            _inner = inner;
        }

        /// <summary>Vanilla (or already-registered) prefab to clone, identified by GUID.</summary>
        public ItemDefinitionBuilder CloneOf(PrefabGUID source)
        {
            _inner.CloneOf(source);
            return this;
        }

        /// <summary>
        /// Vanilla prefab to clone, identified by spawnable name
        /// (for example <c>Item_Ingredient_Crystal</c>). Resolved at apply time, so a plugin can
        /// call this before the collection is populated.
        /// </summary>
        public ItemDefinitionBuilder CloneOf(string spawnableName)
        {
            _inner.CloneOf(spawnableName);
            return this;
        }

        /// <summary>Override the clone's stack size. Omit to keep the source's <c>ItemData.MaxAmount</c>.</summary>
        public ItemDefinitionBuilder MaxAmount(int maxAmount)
        {
            _inner.MaxAmount(maxAmount);
            return this;
        }

        /// <summary>
        /// Display name sent to the client (64-byte ASCII field). The client registers it under
        /// the definition id and points the cloned <c>ManagedItemData</c> at that key so inventory
        /// UI shows this text instead of the source prefab's name.
        /// </summary>
        public ItemDefinitionBuilder Named(string localizedName)
        {
            _inner.Named(localizedName);
            return this;
        }

        /// <summary>
        /// Tooltip text sent to the client (1-63 ASCII, same wire slot size as the name), registered
        /// under <c>&lt;id&gt;.desc</c>. Omit it and a plugin <c>en.json</c> entry with that key is used instead.
        /// </summary>
        public ItemDefinitionBuilder Described(string description)
        {
            _inner.Described(description);
            return this;
        }

        /// <summary>
        /// Worn graphics for this item. Same as
        /// <see cref="PrefabRegistry.ItemDefinitionBuilder.Wearable"/>. The client builds the
        /// sketch on the wearer while the item is in its equipment slot.
        /// </summary>
        public ItemDefinitionBuilder Wearable(WearableGraphics graphics)
        {
            _inner.Wearable(graphics);
            return this;
        }

        /// <summary>
        /// Set the clone's displayed armor level and optionally remove inherited stat bonuses
        /// and equipment set membership. Existing base equip buffs are preserved.
        /// </summary>
        public ItemDefinitionBuilder ArmorBaseline(int displayedLevel, bool removeBonuses = true, bool removeSet = true)
        {
            _inner.ArmorBaseline(displayedLevel, removeBonuses, removeSet);
            return this;
        }

        /// <summary>Add bounded equipment stat bonuses to this registered clone.</summary>
        public ItemDefinitionBuilder StatBonuses(Progenitor.Content.ItemStatBonuses bonuses)
        {
            _inner.StatBonuses(bonuses);
            return this;
        }

        /// <summary>
        /// Keep this item on death (<c>ItemCategory.BloodBound</c> on the clone and its lookup
        /// row). Same as <see cref="PrefabRegistry.ItemDefinitionBuilder.BloodBound"/>.
        /// </summary>
        public ItemDefinitionBuilder BloodBound()
        {
            _inner.BloodBound();
            return this;
        }

        /// <summary>
        /// Never lose durability, including on death; existing server instances are rearmed.
        /// Same as <see cref="PrefabRegistry.ItemDefinitionBuilder.Indestructible"/>.
        /// </summary>
        public ItemDefinitionBuilder Indestructible()
        {
            _inner.Indestructible();
            return this;
        }

        /// <summary>Inventory PNG relative to this plugin's assets folder; loaded on the client.</summary>
        public ItemDefinitionBuilder Icon(string relativePath)
        {
            _inner.Icon(relativePath);
            return this;
        }

        /// <summary>
        /// Accepts the definition under this plugin. Dispose (or unload) removes it from every
        /// lookup and from the client-sync list. No owner argument: the context's plugin is used.
        /// </summary>
        public IDisposable Register()
        {
            var handle = _inner.Register(_ctx.Owner);
            _ctx.Track(handle);
            return handle;
        }
    }

    /// <summary>
    /// Fluent clone-buff builder bound to a <see cref="PluginContext"/>. Mirrors
    /// <see cref="PrefabRegistry.BuffDefinitionBuilder"/>; the only difference is
    /// <see cref="Register"/> takes no owner. No <c>MaxAmount</c>.
    /// </summary>
    public sealed class BuffDefinitionBuilder
    {
        private readonly PluginContext _ctx;
        private readonly PrefabRegistry.BuffDefinitionBuilder _inner;

        internal BuffDefinitionBuilder(PluginContext ctx, PrefabRegistry.BuffDefinitionBuilder inner)
        {
            _ctx = ctx;
            _inner = inner;
        }

        /// <summary>Vanilla (or already-registered) buff prefab to clone, identified by GUID.</summary>
        public BuffDefinitionBuilder CloneOf(PrefabGUID source)
        {
            _inner.CloneOf(source);
            return this;
        }

        /// <summary>
        /// Vanilla buff to clone, identified by spawnable name
        /// (for example <c>Buff_General_Haste</c>). Resolved at apply time.
        /// </summary>
        public BuffDefinitionBuilder CloneOf(string spawnableName)
        {
            _inner.CloneOf(spawnableName);
            return this;
        }

        /// <summary>Display name sent to the client (1–63 ASCII). HUD icon still comes from the source.</summary>
        public BuffDefinitionBuilder Named(string localizedName)
        {
            _inner.Named(localizedName);
            return this;
        }

        /// <summary>Tooltip text sent to the client (1–63 ASCII).</summary>
        public BuffDefinitionBuilder Described(string description)
        {
            _inner.Described(description);
            return this;
        }

        /// <summary>
        /// Client HUD icon: a PNG path relative to this plugin's <c>assets</c> folder
        /// (for example <c>icons/nodrain.png</c>). Absolute paths and <c>..</c> are refused.
        /// The client loads the file after the clone; the path is not a wire field.
        /// </summary>
        public BuffDefinitionBuilder Icon(string relativePath)
        {
            _inner.Icon(relativePath);
            return this;
        }

        /// <summary>
        /// Opt-in: destroy-and-reapply live instances of this buff after a later
        /// definition change. Default off. Same contract as
        /// <see cref="PrefabRegistry.BuffDefinitionBuilder.RefreshLiveInstances"/>.
        /// </summary>
        public BuffDefinitionBuilder RefreshLiveInstances()
        {
            _inner.RefreshLiveInstances();
            return this;
        }

        /// <summary>
        /// Accepts the definition under this plugin. Dispose (or unload) removes it from the
        /// client-sync list. No owner argument: the context's plugin is used.
        /// </summary>
        public IDisposable Register()
        {
            var handle = _inner.Register(_ctx.Owner);
            _ctx.Track(handle);
            return handle;
        }
    }
}

/// <summary>
/// Castle-read facade bound to one plugin's side world. Pass-through to
/// <see cref="CastleReader"/> so a mod does not have to pick <c>Worlds.Server</c> itself.
/// </summary>
public sealed partial class PluginCastles
{
    private readonly PluginContext _ctx;
    internal PluginCastles(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Every plot on this plugin's world. Empty when the world is not ready. Same contract as
    /// <see cref="CastleReader.Plots"/> — missing components never throw.
    /// </summary>
    public IReadOnlyList<CastlePlot> Plots()
    {
        var world = _ctx.World;
        return world == null ? Array.Empty<CastlePlot>() : CastleReader.Plots(world);
    }

    /// <summary>Floor tiles on <paramref name="plot"/>, row-major. Empty when the world is not ready.</summary>
    public IReadOnlyList<CastleTile> Tiles(CastlePlot plot)
    {
        var world = _ctx.World;
        return world == null ? Array.Empty<CastleTile>() : CastleReader.Tiles(world, plot);
    }

    /// <summary>Placed buildings on <paramref name="plot"/>, sorted by network id. Empty when the world is not ready.</summary>
    public IReadOnlyList<CastleBuilding> Buildings(CastlePlot plot)
    {
        var world = _ctx.World;
        return world == null ? Array.Empty<CastleBuilding>() : CastleReader.Buildings(world, plot);
    }

    /// <summary>
    /// Plot that owns <paramref name="heartOrTerritory"/> (heart, territory, or a connected
    /// tile-model). Null when the world is not ready or the entity is not on a plot.
    /// </summary>
    public CastlePlot? PlotOf(Entity heartOrTerritory)
    {
        var world = _ctx.World;
        return world == null ? null : CastleReader.PlotOf(world, heartOrTerritory);
    }

    // ---- handles (Progenitor.Castles.Castles) -------------------------------------------------
    // The rows above are the raw reader. Everything below is the handle API: game-free info
    // types, IsInside, CanPlace, and the CastleHeartPlaced / CastleHeartDestroyed events. It is
    // server-only and never null — the client gets disabled handles and one Debug line, the same
    // contract as ctx.Effects and ctx.Units — and read-only, so nothing here is Track()ed.

    /// <summary>Every castle on this server, ordered by territory index. Empty on the client.</summary>
    public IReadOnlyList<ICastleHandle> All()
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.All;
    }

    /// <summary>
    /// The castle owned by <paramref name="userEntity"/> or their clan. Never null: a player
    /// with no castle gets a disabled handle, so <c>if (castle.Enabled)</c> is the only check.
    /// </summary>
    public ICastleHandle ForUser(Entity userEntity)
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.ForUser(CastleId.From(userEntity));
    }

    /// <summary>Every castle owned by <paramref name="userEntity"/> or their clan.</summary>
    public IReadOnlyList<ICastleHandle> AllForUser(Entity userEntity)
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.AllForUser(CastleId.From(userEntity));
    }

    /// <summary>The castle holding <paramref name="territoryIndex"/>, or a disabled handle.</summary>
    public ICastleHandle ForTerritory(int territoryIndex)
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.ForTerritory(territoryIndex);
    }

    /// <summary>The castle whose territory contains this world position, or a disabled handle.</summary>
    public ICastleHandle At(CastleVec3 position)
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.At(position);
    }

    /// <summary>
    /// The castle whose territory contains this entity, or a disabled handle. For a character, the
    /// castle of <see cref="StandingTerritory"/>: every floor counts and a teleport shows on the next read.
    /// </summary>
    public ICastleHandle At(Entity entity)
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.At(CastleId.From(entity));
    }

    /// <summary>
    /// Territory index of the castle plot <paramref name="character"/> stands on, or -1 (open world,
    /// unknown entity, client). The same value <c>MaterialContext.StandingTerritory</c> carries, so a
    /// mod's actions and its material views agree. See <see cref="Progenitor.Castles.Castles.StandingTerritory(CastleId)"/>.
    /// </summary>
    public int StandingTerritory(Entity character)
    {
        _ctx.EnsureAttached();
        return Progenitor.Castles.Castles.StandingTerritory(CastleId.From(character));
    }
}

/// <summary>
/// Chat-send facade bound to one plugin's side world. Pass-through to
/// <see cref="Progenitor.Chat.Chat.Broadcast"/> / <see cref="Progenitor.Chat.Chat.SendTo"/> so a
/// mod does not have to pick <c>Worlds.Server</c> itself. Not recorded for undo: each call
/// creates game event entities that <c>SerializeAndSendServerEventsSystem</c> consumes the same
/// frame.
/// </summary>
public sealed class PluginChat
{
    private readonly PluginContext _ctx;
    internal PluginChat(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Server: send <paramref name="text"/> (as <c>&lt;Mod Name&gt;: text</c>, WINDOWS1) to every <c>ConnectedUser</c> on this plugin's
    /// world, including the speaker. Returns 0 when the world is not ready or this is the
    /// client process.
    /// </summary>
    public int Broadcast(string text, ChatMessageType type = ChatMessageType.System)
    {
        var world = _ctx.World;
        return world == null ? 0 : Broadcast(text, prefix: true, type);
    }

    /// <summary>
    /// Server: send <paramref name="text"/> (as <c>&lt;Mod Name&gt;: text</c>, WINDOWS1) to one user entity that carries
    /// <c>ConnectedUser</c>. Returns false when the world is not ready or the entity has no
    /// connected-user slot.
    /// </summary>
    public bool SendTo(Entity user, string text, ChatMessageType type = ChatMessageType.System)
    {
        var world = _ctx.World;
        return world != null && SendTo(user, text, prefix: true, type);
    }

    /// <summary>
    /// WINDOWS1: <see cref="Broadcast(string, ChatMessageType)"/> choosing whether the mod's name goes in front.
    /// Both forms put <c>&lt;Mod Name&gt;: </c> in front of the text by default (not twice: a text that already starts
    /// with the name is sent as it is); <paramref name="prefix"/> false sends the text exactly as given. A text longer
    /// than one chat line (500 UTF-8 bytes with the name) is sent as several lines, the name on the first, as a command
    /// reply is, instead of being cut. Returns the recipients of the first line.
    /// </summary>
    public int Broadcast(string text, bool prefix, ChatMessageType type = ChatMessageType.System)
    {
        var world = _ctx.World;
        if (world == null) return 0;
        int first = -1;
        foreach (string line in ModMessagePrefix.ReplyChunks(_ctx.Name, text, prefix))
        {
            int n = Progenitor.Chat.Chat.Broadcast(world, line, type);
            if (first < 0) first = n;
        }

        return first < 0 ? 0 : first;
    }

    /// <summary>
    /// WINDOWS1: <see cref="SendTo(Entity, string, ChatMessageType)"/> choosing whether the mod's name goes in front; a long
    /// text goes as several lines, as with <see cref="Broadcast(string, bool, ChatMessageType)"/>. True when every line was sent.
    /// </summary>
    public bool SendTo(Entity user, string text, bool prefix, ChatMessageType type = ChatMessageType.System)
    {
        var world = _ctx.World;
        if (world == null) return false;
        bool all = true, any = false;
        foreach (string line in ModMessagePrefix.ReplyChunks(_ctx.Name, text, prefix))
        {
            any = true;
            all &= Progenitor.Chat.Chat.SendTo(world, user, line, type);
        }

        return any && all;
    }
}

internal sealed class DisposableBag
{
    private readonly List<IDisposable> _items = new();
    private readonly object _lock = new();
    private volatile bool _undone;

    public bool IsUndone => _undone;

    public void Add(IDisposable disposable)
    {
        lock (_lock)
        {
            if (_undone)
            {
                try { disposable.Dispose(); }
                catch { /* already tearing down; nothing to report to */ }
                return;
            }
            _items.Add(disposable);
        }
    }

    public bool Remove(IDisposable disposable)
    {
        lock (_lock) return _items.Remove(disposable);
    }

    public void UndoAll(ManualLogSource? log)
    {
        IDisposable[] snapshot;
        lock (_lock)
        {
            _undone = true;
            snapshot = _items.ToArray();
            _items.Clear();
        }

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { log?.LogError($"plugin registration dispose threw: {e}"); }
        }
    }
}

internal sealed class ActionDisposable : IDisposable
{
    private Action? _action;

    public ActionDisposable(Action action) => _action = action ?? throw new ArgumentNullException(nameof(action));

    public void Dispose()
    {
        var action = Interlocked.Exchange(ref _action, null);
        action?.Invoke();
    }
}

internal sealed class NopDisposable : IDisposable
{
    public static readonly NopDisposable Instance = new();
    public void Dispose() { }
}
