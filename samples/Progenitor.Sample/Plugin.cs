using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using ProjectM;
using ProjectM.Network;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Persistence;
using Progenitor.Plugins;
using Progenitor.Systems;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Sample;

/// <summary>
/// First real consumer of the framework and the M1 acceptance test. One DLL for both processes:
/// the server registers the item, commands, join counter, and handshake requirement; the client
/// registers the same item and announces itself so a peer without this plugin is kicked.
/// <see cref="NetworkCompatibilityAttribute"/> makes that requirement explicit: both sides must
/// have this plugin at the same version (the handshake's original exact-match rule).
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
public sealed class Plugin : ProgenitorPlugin<SampleContext>
{
    internal static ManualLogSource? Logger { get; private set; }

    /// <summary>
    /// Attached context for static command methods that need <see cref="PluginContext.Net"/>.
    /// </summary>
    internal static SampleContext? Active { get; private set; }

    /// <summary>
    /// This plugin's save bag, captured from <see cref="PluginContext.Save"/> so static command
    /// methods can read it without holding the context.
    /// </summary>
    internal static IModStore? Store { get; private set; }

    /// <summary>
    /// This plugin's server-level bag, captured from <see cref="PluginContext.ServerSave"/>: data
    /// that survives wipes and world restores (the all-time join book, <c>.store server</c>).
    /// </summary>
    internal static IModStore? ServerStore { get; private set; }

    /// <summary>
    /// Server-authoritative max stack for the cloned Ember Shard. Bound with <c>synced: true</c>
    /// so a handshaken client locks to the server cfg until disconnect.
    /// </summary>
    internal static ConfigEntry<int>? EmberStack { get; private set; }

    private EventHandler? _emberStackChanged;

    /// <summary>
    /// Spawnable id hashed into the Ember Shard GUID. Must be identical on every peer; the
    /// registry derives <see cref="PrefabGUID"/> from this string rather than a handshake.
    /// </summary>
    public const string EmberId = "sample.ember";

    /// <summary>Vanilla Crystal (<c>Item_Ingredient_Crystal</c>), the clone source from spike 2.</summary>
    public static readonly PrefabGUID EmberSource = new(-257494203);

    /// <summary>Deterministic GUID for the cloned item. Same value the registry will assign.</summary>
    public static PrefabGUID EmberGuid => new(PrefabGUID.CalculateGuidHash(EmberId));

    /// <summary>
    /// Clone-time stack size written by <c>DefineItem(...).MaxAmount</c>. This is the before-form
    /// of the edit: the applicator patches <c>ItemData.MaxAmount</c> while cloning. Live value after
    /// the ordered editors below is <see cref="CurrentEmberStack()"/>.
    /// </summary>
    public const int EmberStackBefore = 50;

    /// <summary>
    /// Default-priority editor writes this first. Late overwrites it; this value is the loser of
    /// the intentional conflict and must not appear as <c>itemMap max</c> once both editors have run.
    /// </summary>
    public const int EmberStackDefaultEdit = 20;

    /// <summary>
    /// Default of the synced <see cref="EmberStackSection"/>.<see cref="EmberStackKey"/> entry,
    /// and the Late editor's fallback when that entry is not bound yet. Last writer of
    /// <c>ItemData.MaxAmount</c> unless the server cfg overrides it; <c>status</c> reports
    /// <c>itemMap max</c> from the live entity, and <c>.ember</c> prints <c>max=</c>.
    /// </summary>
    public const int EmberStackAfter = 99;

    /// <summary>BepInEx section for the synced ember stack size (<c>Sample.EmberStack</c>).</summary>
    public const string EmberStackSection = "Sample";

    /// <summary>BepInEx key for the synced ember stack size. Default <see cref="EmberStackAfter"/>.</summary>
    public const string EmberStackKey = "EmberStack";

    /// <summary>
    /// Plugin id of the WorldReady handler that must run first. Registered second (priority
    /// <see cref="OrderFirstPriority"/>) so a numeric sort would put it last; the second
    /// handler's <c>after</c> constraint flips that. See <c>docs/hooks-ordering.md</c>.
    /// </summary>
    public const string OrderFirstId = "progenitor.sample.order.first";

    /// <summary>
    /// Plugin id of the WorldReady handler that must run second. Registered first at
    /// <see cref="OrderSecondPriority"/> (would win on number alone) with
    /// <c>after=[<see cref="OrderFirstId"/>]</c>.
    /// </summary>
    public const string OrderSecondId = "progenitor.sample.order.second";

    /// <summary>Numeric priority of the first-running WorldReady handler (the lower number).</summary>
    public const int OrderFirstPriority = 0;

    /// <summary>Numeric priority of the second-running WorldReady handler (the higher number).</summary>
    public const int OrderSecondPriority = 100;

    /// <summary>
    /// Distinct plugin id for the Late editor. Core skips a conflict Warn when both writers share
    /// a plugin id (same-plugin stacking is silent last-writer-wins); this id is owned by the
    /// Sample plugin so unload still reverses it, and is different so the documented conflict
    /// line is actually logged.
    /// </summary>
    public const string EmberLateEditId = "progenitor.sample.late";

    /// <summary>Display name paired with <see cref="EmberLateEditId"/> on the conflict Warn.</summary>
    public const string EmberLateEditName = "Sample Late";

    /// <inheritdoc />
    protected override void OnLoad(SampleContext ctx)
    {
        Logger = ctx.Log;
        Store = ctx.Save;
        ServerStore = ctx.ServerSave;
        Active = ctx;
        SampleFx.Attach(ctx);
        SampleMenu.Register(ctx);

        // Synced stack size (docs/config-sync.md). Server cfg is authoritative after handshake;
        // the Late editor reads this instead of a hard-coded 99. Client locks until disconnect.
        EmberStack = ctx.Bind(
            EmberStackSection,
            EmberStackKey,
            EmberStackAfter,
            "Max stack for sample.ember after the Late editor. Server pushes this to clients after handshake.",
            synced: true);
        _emberStackChanged = OnEmberStackChanged;
        EmberStack.SettingChanged += _emberStackChanged;

        // Before-form: clone-time MaxAmount is written onto ItemData when the registry applies
        // the clone (PrefabApplicator). After-form: Content.Edit mutates the live prefab entity
        // in a later ordered pass. Apply-before-packet: the server runs that pass before PrfD;
        // the client typically applies on the first packet (local cfg) then handshake SettingChanged
        // Invalidates so Late writes the server value. Both sides run the pass locally, and since
        // job FEDW1 the server's results also travel: the components its pass touched ship as a
        // PrfE layer that the client applies last, over its own editors (docs/edits-on-the-wire.md).
        // ServerMustHave is still required — a client with no plugin at all is not running the mod
        // — but a client whose editor read a different input now converges on the server's values.
        ctx.Content.DefineItem(EmberId)
            .CloneOf(EmberSource)
            .MaxAmount(EmberStackBefore)
            .Named("Ember Shard")
            .Register();

        // The debug buff (.nodrain) and its map powers moved into Core as `.debug` (2026-10-06).
        SampleRecipe.Register(ctx);
        SampleEditLayers.Register(ctx);

        ctx.Content.Edit(EmberGuid, EditEmberStackDefault, PrefabEditPriority.Default);

        // Intentional second editor at Late on the same ItemData so last-writer-wins is
        // visible. Distinct pluginId (same owner) is required for Core to emit the conflict
        // Warn; see EmberLateEditId. Late wins: MaxAmount becomes CurrentEmberStack()
        // (Sample.EmberStack, default EmberStackAfter).
        ctx.Track(PrefabRegistry.Edit(
            EmberGuid,
            EditEmberStackLate,
            ctx.Owner,
            PrefabEditPriority.Late,
            pluginId: EmberLateEditId,
            pluginName: EmberLateEditName));

        int late = CurrentEmberStack();
        ctx.Log.LogInfo(
            $"Sample: editing {EmberId} ItemData.MaxAmount {EmberStackBefore} -> {late} "
            + $"(Default {EmberStackDefaultEdit} then Late {late} from {EmberStackSection}.{EmberStackKey}; conflict is intentional).");

        // Hook / event order (docs/hooks-ordering.md). ctx.Events.On has a before/after overload
        // too (PluginContext.cs:472); this pair deliberately shows the static EventBus.On + Track
        // form, which is what a plugin needs when it wants to name the owner itself. Either route
        // takes the same before/after arrays. Register the higher priority first (would run first by
        // number), then the lower; after=[OrderFirstId] makes the second registration run first.
        ctx.Track(EventBus.On<WorldReady>(
            OnWorldReadySecond,
            priority: OrderSecondPriority,
            owner: ctx.Owner,
            after: new[] { OrderFirstId },
            pluginId: OrderSecondId));
        ctx.Track(EventBus.On<WorldReady>(
            OnWorldReadyFirst,
            priority: OrderFirstPriority,
            owner: ctx.Owner,
            pluginId: OrderFirstId));
        ctx.Log.LogInfo("Sample: " + EventBus.DescribeOrder<WorldReady>().Replace("\n", " | "));

        // Job J19. MUST stay on the line above DeclareSelf(): SampleHandshake exercises
        // RequireSelf()/ProvideSelf(), which record an *undeclared* row for this plugin's own
        // guid, and DeclareSelf() then overwrites it with the declared ServerMustHave/Exact one.
        // Moving this below DeclareSelf() would silently downgrade the Sample's compatibility to a
        // one-direction exact match. SampleHandshakeTests pins the order in this file.
        SampleHandshake.Attach(ctx);
        ctx.Handshake.DeclareSelf();

        // Job FEX1. The client→server round trip is the one Sample feature that is registered on
        // both sides from one call: the server takes the packet, the client owns the F8 keybind
        // that sends it and the handler that logs the answer.
        SampleNet.Attach(ctx);

        // ctx.Input (docs/input.md). Also both sides from one call: the server registers `.input`,
        // the client takes the input leases the command asks for. See SampleInput.cs.
        SampleInput.Attach(ctx);

        // ctx.Camera. Server registers `.shot` and sends SmSh; the client applies the shot.
        SampleCamera.Attach(ctx);

        // Feature negotiation (ctx.Features, ctx.Peer, ctx.Peers): the client offers "camera" for the
        // .shot packet; the server registers `.compat`, which prints both sides' views. See SampleCompat.cs.
        SampleCompat.Attach(ctx);

        // ctx.Requests and ctx.Subscriptions: the server registers `.request` and `.subscribe`, a request handler and the
        // `board` topic; the client sends the request or subscribes when the server asks it to. See SampleExchange.cs.
        SampleExchange.Attach(ctx);

        // Map-scope keybinds (ctx.Keybinds.RegisterInMap / RegisterHoldInMap) and the WorldMap
        // pointer: a client-only map ruler. See SampleMap.cs.
        SampleMap.Attach(ctx);
        SampleFrostNovaAppearance.Attach(ctx);
        SampleCatalogRecipeExamples.Attach(ctx);

        // Job FRIFT1. Rift Incursion timers, also registered on both sides from one call: the
        // server reads WarEventSystem's scheduler and broadcasts SmRf, the client holds the packet
        // and renders two countdown rows beside the vanilla map card. See SampleRifts.cs.
        SampleRifts.Attach(ctx);

        // Job FEX3. The programmatic localization path runs on both sides on purpose: the server
        // records the string, the client also writes it into the game's own table and logs the
        // read-back at world-ready. One call, two behaviors, both worth seeing.
        SampleLocalization.Attach(ctx);

        if (ctx.IsServer)
        {
            // Job FEX3. Written here, in OnLoad, because that is where a mod naturally counts its
            // own loads - and on a server start the world save folder is not known yet, so this
            // is the pending-store merge doing its job. `.store` reports it.
            SampleStore.Attach(ctx.Save);

            // Job FEX3. Subscribers for the four unit events .rat has always published and never
            // listened to. Owner-scoped, so Core's sweep would drop them even if Detach did not.
            SampleUnitEvents.Subscribe(ctx.Owner, ctx.Guid);

            // C# static classes cannot be generic type arguments; Register(Type) is the path.
            ctx.Commands.Register(typeof(SampleCommands));
            ctx.Commands.Register(typeof(SampleMenuCommands));
            SampleRouting.Attach(ctx);
            ctx.Events.On<PlayerConnected>(OnPlayerConnected);
            ctx.Systems.Add("sample.users", OnUsersTick);

            // Job FEX1. ctx.Hooks (Before/After on a managed and a Burst system, plus a bounded
            // Skip) and ctx.Chat (broadcast, whisper, and a subscriber that watches the
            // cancellable ChatMessage event). Both are server-only facades.
            SampleHooks.Attach(ctx);
            SampleChat.Attach(ctx);

            // Job J14. The entity toolkit's unexampled routes (.ecs: Query.Build with an explicit
            // dispose, Singletons.TryGetEntity/TryGetRawRW) and the Players module (.kick, admin
            // only: PlayerControl.TryGetConnection + Disconnect; the reason text is logged, not shown).
            SampleEcs.Attach(ctx);
            SamplePlayers.Attach(ctx);
            // Milestone P-a: logs PlayerReady and PlayerTerritoryChanged for the .items acceptance.
            SampleInventory.Attach(ctx);

            // Job FCFGEX. ConfigHelper: Bind, BindEnum/ReadEnum, SharedFileName, and the disk
            // Watch whose whole point is an operator editing this plugin's GUID.cfg by hand while
            // the server runs. Server-side, because that file is on the server. See `.cfg`.
            SampleConfig.Attach(ctx);

            // Job FEX5. The event types nothing subscribed to: PlayerDisconnected, WorldLoaded,
            // the ICancellable half of ChatMessage, and - only once `.events castle on` asks for
            // them - CastleHeartPlaced/Destroyed, whose poll runs only while subscribed.
            SampleEvents.Attach(ctx);

            // Job FEX5. A [CommandGroup] registered as an *instance* (ctx.Commands.Register(object))
            // so a stateful command class is shown, with a [Remainder] member and one that reads
            // ctx.RawText / ctx.IsAdmin. Everything else here is a static class + Register(Type).
            SampleGroup.Attach(ctx);

            // Job J21. The `.access <form>` subcommands: Access.<Type>.Type, the method
            // MethodBase/pointer pair, and a deliberate miss that prints the AccessException.
            SampleAccessDemo.Attach(ctx);

            // Job J18. The virtual-system anchors nothing used (AddAfter / AddBefore / an explicit
            // VirtualSystemAnchor), ctx.Systems.RemoveAll()/Describe(), and the two SystemHooks
            // members nothing called (WhenWorldExists, RemoveAll(owner)). Drives `.sys`.
            SampleSystems.Attach(ctx);

            // Job J18. A stateful command class registered as an instance beside the type and
            // generic forms, so `.counter` shows what instance state buys a command class.
            SampleInstanceCommands.Attach(ctx);

            // Frost dash: the boss's native ring payload at both edges of the travel cast, every
            // time the player dashes. No second cast or proxy character.
            SampleFrostDash.Attach(ctx);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Core-owned handles (panels, server beams, spawned units, commands, events, keybinds, patches,
    /// god-mode guards, prefab registrations) are swept by Core on every unload reason, so nothing
    /// here is reason-aware. The debug buff that used to be the reason-aware case is Core's now
    /// (<c>.debug</c>, docs/modules.md "Debug tools").
    /// </remarks>
    protected override void OnUnload(SampleContext ctx, PluginUnloadReason reason)
    {
        if (EmberStack != null && _emberStackChanged != null)
            EmberStack.SettingChanged -= _emberStackChanged;
        _emberStackChanged = null;
        EmberStack = null;
        // Job FSAMP. One line naming the reason and what this plugin decided to do about it, next
        // to Core's own `Reload: Progenitor Sample unload reason=X (source)` line. The rule is
        // docs/tutorial.md §7: keep on Swap / Shutdown / Unknown, sweep only on Removal, because
        // a wrong keep is recoverable and a wrong sweep is not. Everything Core owns - commands,
        // events, keybinds, panels, patches, god-mode guards, prefab registrations - is swept for
        // us on every reason regardless, so the decision below is only about the buff.
        SampleLog.Info("unload", SampleLog.UnloadMessage(reason));

        SampleFx.Detach();
        SampleBeam.Detach();
        SamplePatchDemo.Detach();
        SampleEcs.Detach();
        SamplePlayers.Detach();
        SampleInventory.Detach();
        SampleServants.Detach();
        SampleLogPanel.Detach();
        SampleEpoch.Reset();
        SampleHooks.Detach();
        SampleChat.Detach();
        SampleConfig.Detach();
        SampleNet.Detach();
        SampleInput.Detach();
        SampleCamera.Detach();
        SampleForms.Detach();
        SampleMap.Detach();
        // Job J19. SampleStore.Detach also calls ModSave.RemoveAll(owner) and logs the count; it
        // must run while Plugin.Active is still set, which is why both sit here and not after the
        // `Active = null` below.
        SampleStore.Detach();
        SampleHandshake.Detach();
        SampleRifts.Detach();
        SampleLocalization.Detach();
        SampleEvents.Detach();
        SampleGroup.Detach();
        SampleAccessDemo.Detach();
        SampleFrostDash.Detach();
        SampleFrostNovaAppearance.Detach();
        SampleInstanceCommands.Detach();
        // Job J18. Last of the command/hook teardowns on purpose: its SystemHooks.RemoveAll(owner)
        // is assembly-scoped, so it also drops SampleHooks' hooks a moment before Core's sweep
        // would have. That is only ever acceptable here, during unload.
        SampleSystems.Detach();
        SampleContent.Detach();
        SampleUnitEvents.Unsubscribe();
        SampleLog.Clear();
        Active = null;
        Store = null;
        ServerStore = null;
        Logger = null;
    }

    private void OnPlayerConnected(PlayerConnected ev)
    {
        try
        {
            var ctx = Context;
            if (ctx == null) return;

            string name = ev.CharacterName;
            string platformId = ev.User.PlatformId.ToString();
            // The same book twice: in this world (ctx.Save, archived by a wipe and rolled back by
            // a restore) and on this server (ctx.ServerSave, which neither touches).
            int joins = SampleJoins.Record(ctx.Save, platformId, name);
            int allTime = SampleJoins.Record(ctx.ServerSave, platformId, name);
            ctx.Log.LogInfo($"Welcome, {name}. This is join #{joins} in this world, #{allTime} on this server (platform {platformId}).");
        }
        catch (Exception e)
        {
            Log.LogError($"Sample PlayerConnected handler threw: {e}");
        }
    }

    /// <summary>
    /// Default-priority pass: writes <see cref="EmberStackDefaultEdit"/> onto the clone's
    /// <c>ItemData</c>. ItemData is not C# unmanaged (bool fields), so this uses
    /// <see cref="PrefabEditHandle.GetComponent{T}"/> / <see cref="PrefabEditHandle.SetComponent{T}"/>
    /// rather than <c>Read</c>/<c>Write</c>.
    /// </summary>
    private static void EditEmberStackDefault(PrefabEditHandle prefab)
    {
        WriteEmberStack(prefab, EmberStackDefaultEdit, "Default");
    }

    /// <summary>
    /// Late-priority pass: last writer of <c>ItemData</c>, so this is the live stack size.
    /// Reads <see cref="EmberStack"/> (synced <c>Sample.EmberStack</c>) rather than a hard-coded 99.
    /// </summary>
    private static void EditEmberStackLate(PrefabEditHandle prefab)
    {
        WriteEmberStack(prefab, CurrentEmberStack(), "Late");
    }

    /// <summary>Live Late value: the synced entry, or <see cref="EmberStackAfter"/> if unbound.</summary>
    internal static int CurrentEmberStack()
    {
        var entry = EmberStack;
        if (entry == null) return EmberStackAfter;
        try { return entry.Value; }
        catch { return EmberStackAfter; }
    }

    /// <summary>
    /// First-running WorldReady handler. Registered second at priority 0; the other handler's
    /// <c>after</c> constraint is what puts this first. Grep <c>Sample: WorldReady order=1</c>.
    /// </summary>
    private void OnWorldReadyFirst(WorldReady ev)
    {
        try
        {
            Log.LogInfo($"Sample: WorldReady order=1 id={OrderFirstId} world={ev.World.Name}");
        }
        catch (Exception e)
        {
            Log.LogError($"Sample WorldReady order=1 threw: {e}");
        }
    }

    /// <summary>
    /// Second-running WorldReady handler. Registered first at priority 100 with
    /// <c>after=[OrderFirstId]</c>. Grep <c>Sample: WorldReady order=2</c>.
    /// </summary>
    private void OnWorldReadySecond(WorldReady ev)
    {
        try
        {
            Log.LogInfo($"Sample: WorldReady order=2 id={OrderSecondId} world={ev.World.Name}");
        }
        catch (Exception e)
        {
            Log.LogError($"Sample WorldReady order=2 threw: {e}");
        }
    }

    private void OnEmberStackChanged(object? sender, EventArgs e)
    {
        try
        {
            var ctx = Context;
            if (ctx == null) return;
            int value = CurrentEmberStack();
            // ConfigSync sets Locked after BoxedValue, so IsLocked can still be false on the
            // apply SettingChanged. Log the client line whenever the value lands from the
            // server (apply or a later push); refused locked writes do not fire this.
            if (ctx.IsClient)
                ctx.Log.LogInfo($"Sample: EmberStack applied/locked to {value}");
            else
                ctx.Log.LogInfo($"Sample: EmberStack changed to {value}; invalidated edits");
            // Do not write the clone here. CollectionReady will not re-run: the fingerprint
            // is plugin id + priority + index (R20 finding 4). InvalidateEdits bumps the
            // generation and re-runs the ordered pass so Late writes MaxAmount through
            // PrefabEditHandle.SetComponent (ItemHashLookupMap included).
            ctx.Content.InvalidateEdits(ctx.Owner);
        }
        catch (Exception ex)
        {
            Log.LogError($"Sample EmberStack SettingChanged threw: {ex}");
        }
    }

    private static void WriteEmberStack(PrefabEditHandle prefab, int maxAmount, string which)
    {
        try
        {
            if (!prefab.Has<ItemData>()) return;
            var data = prefab.GetComponent<ItemData>();
            data.MaxAmount = maxAmount;
            prefab.SetComponent(data);
        }
        catch (Exception e)
        {
            Logger?.LogError($"Sample ember {which} edit threw: {e}");
        }
    }

    /// <summary>
    /// Cheap heartbeat: every 300 simulation ticks, count connected users. Uses the virtual-system
    /// frame counter because <c>World.Time.ElapsedTime</c> reads 0 through the interop.
    /// </summary>
    private void OnUsersTick(VirtualSystemContext vs)
    {
        if (vs.Frame % 300 != 0) return;
        try
        {
            var em = vs.EntityManager;
            var users = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            int connected = 0;
            for (int i = 0; i < users.Count; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                if (user.IsConnected) connected++;
            }
            Log.LogInfo($"sample.users: {connected} connected user(s) (frame {vs.Frame}).");
        }
        catch (Exception e)
        {
            Log.LogError($"sample.users threw: {e}");
        }
    }
}

/// <summary>
/// Per-plugin bag. Empty on purpose: setup lives in <see cref="Plugin.OnLoad"/> because the
/// wrappers are attached after construction.
/// </summary>
public sealed class SampleContext : PluginContext
{
}

/// <summary>Per-save join book stored under <see cref="ModSave.Store"/>.</summary>
internal sealed class JoinBook
{
    public List<JoinRecord> Players { get; set; } = new();
}

/// <summary>One player's running join count, keyed by platform id so a rename does not reset it.</summary>
internal sealed class JoinRecord
{
    public string PlatformId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>Read/write the join book. The file lives with the world save, not with this plugin.</summary>
internal static class SampleJoins
{
    /// <summary>
    /// The store key. Declared in <see cref="SampleStore"/> so <c>.store reset</c> and this
    /// reader name the same string, and so the game-free tests can reach it (job FEX3).
    /// </summary>
    internal const string Key = SampleStore.JoinsKey;

    internal static JoinBook Load(IModStore store)
    {
        var book = store.Get(Key, new JoinBook()) ?? new JoinBook();
        book.Players ??= new List<JoinRecord>();
        return book;
    }

    internal static int Record(IModStore store, string platformId, string name)
    {
        var book = Load(store);
        JoinRecord? found = null;
        for (int i = 0; i < book.Players.Count; i++)
        {
            if (book.Players[i].PlatformId == platformId)
            {
                found = book.Players[i];
                break;
            }
        }

        if (found == null)
        {
            found = new JoinRecord { PlatformId = platformId, Name = name, Count = 0 };
            book.Players.Add(found);
        }

        found.Name = name;
        found.Count++;
        store.Set(Key, book);
        return found.Count;
    }
}
