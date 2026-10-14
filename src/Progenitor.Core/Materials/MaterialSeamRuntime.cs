#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Networking;
using Progenitor.Players;
using Unity.Entities;

namespace Progenitor.Materials;

/// <summary>Packed <c>NetworkId</c> of an entity, comparable across the server and the client.</summary>
internal static class MaterialNet
{
    /// <summary>The entity's normal network id packed into a nonzero value; 0 when it has none.</summary>
    internal static ulong Of(EntityManager em, Entity entity)
    {
        try
        {
            if (entity == Entity.Null || !entity.Exists(em) || !entity.Has<NetworkId>(em)) return 0;
            var id = em.GetComponentData<NetworkId>(entity); // Existing typed accessor (Chat, character sheet).
            if (id.Type != NetworkIdType.Normal) return 0;
            ulong packed = ((ulong)(uint)id.Normal_Index << 32) | (uint)id.Normal_Generation;
            return packed == 0 ? ulong.MaxValue : packed;
        }
        catch { return 0; }
    }
}

/// <summary>
/// Binds Core's availability and payment handlers to Bootstrap's opt-in material seams and runs the
/// per-player totals that the client's cost display reads.
///
/// <para><b>Server.</b> A scope is open while <c>StartCraftingSystem</c> (Craft),
/// <c>PlaceTileModelSystem</c> (Build), <c>RepairItemSystem</c> (Repair) or <c>CastleHeartEventSystem</c>
/// (Upgrade) updates; only seam calls inside a scope are routed to the provider, and inside the castle
/// heart system only the calls of an upgrade request (<see cref="MaterialUpgradeFilter"/>). Each system
/// checks affordability and pays in the same synchronous call on the main thread, so one game operation
/// is one <see cref="MaterialService"/> session: the provider is asked once at the check, every line is
/// paid from that answer, and the queued <see cref="MaterialPaid"/> events are published when the
/// system's update ends. Every second (and right after an invalidation or a governed payment) each
/// online player's Build, Craft and Upgrade totals are refreshed and sent when they changed
/// (<see cref="MaterialRefreshPolicy"/>, <see cref="MaterialViewPublisher"/>); a refresh recomputes a
/// view only when something it depends on changed (<see cref="MaterialService.RefreshView"/>).</para>
///
/// <para><b>Client.</b> A received view replaces the shared-storage rows of the game's merged count
/// only for the local character, at the castle heart the view was computed at, and only in its own
/// operation (<see cref="MaterialClientPolicy"/>): items the provider governs show the server's pool
/// amount, every other item keeps the game's own shared amount. Without a matching view the game's own
/// counts are shown. A displayed count is never authorization: the server checks again and pays at
/// action time.</para>
/// </summary>
internal static unsafe class MaterialSeamRuntime
{
    // Shapes identical to Bootstrap's MaterialSeams delegates (re-bound there by CreateDelegate).
    private delegate bool CountGateFn(ulong target, ulong castleHeart);
    private delegate int CountComposeFn(ulong target, ulong castleHeart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity);
    private delegate bool PayGateFn(ulong target, ulong castleHeart, int itemType);
    private delegate int PayFn(ulong target, ulong castleHeart, int itemType, int amount, bool destroyItem, out ulong itemEntity);

    private static MaterialSourceRegistry? _registry;
    private static MaterialService? _service;
    private static UnityMaterialWorld? _world;
    private static readonly MaterialScopeTracker Scope = new();
    private static readonly MaterialUpgradeFilter UpgradeFilter = new();
    private static bool _heartRequestLogged, _foreignCallLogged, _traceNextCheck;

    /// <summary>Development bridge: log the rows the next governed check hands to the game (one shot).</summary>
    internal static void TraceNextCheck() => _traceNextCheck = true;
    private static ulong _repairKey, _heartKey, _repairKeyWorld, _heartKeyWorld;
    private static readonly MaterialViewPublisher Publisher = new();
    private static readonly MaterialRefreshPolicy Refresh = new();
    private static readonly MaterialOverlayStore Overlay = new();
    private static readonly List<IDisposable> Hooks = new();
    private static readonly HashSet<CastleId> Dirty = new();
    private static readonly List<CastleId> Drain = new();
    private static readonly Dictionary<int, long> FullScratch = new();
    private static readonly Dictionary<int, long> BagScratch = new();
    private static readonly List<MaterialLine> ExtraScratch = new();

    private static CountGateFn? _countGate;
    private static CountComposeFn? _compose;
    private static PayGateFn? _payGate;
    private static PayFn? _pay;
    private static bool _bound, _server, _frameHooked, _clientHandler;
    private static string _reason = "not installed";
    private static IDisposable? _disconnected;

    // Client: inside the build-mode placement update, and the build menu's shown state cached per frame.
    private static bool _inBuildMode;
    private static long _buildModeFrame = -1;
    private static long _buildMenuFrame = -1;
    private static bool _buildMenuShown;

    internal static (bool installed, string reason) HookState() => (_bound, _bound ? "" : _reason);

    /// <summary>The server's material service while the seams are bound (development bridge).</summary>
    internal static MaterialService? Service => _service;

    internal static Dictionary<string, object?> DescribeViews()
    {
        var result = new Dictionary<string, object?>();
        foreach (var op in MaterialClientPolicy.ViewOperations)
            result[op.ToString()] = Overlay.TryGet(op, out MaterialView view, out var totals)
                ? new Dictionary<string, object?>
                {
                    ["sequence"] = view.Sequence,
                    ["territory"] = view.Territory,
                    ["heart"] = view.HeartNet,
                    ["station"] = view.StationNet,
                    ["totals"] = new Dictionary<int, int>(totals),
                }
                : null;
        return result;
    }

    internal static void Install(MaterialSourceRegistry registry)
    {
        _registry = registry;
        _server = Plugin.IsServerProcess;
        string bootstrap = BootstrapLink.MaterialSeamsDescribe();
        Plugin.Logger?.LogInfo(bootstrap);

        if (_server)
        {
            _world = new UnityMaterialWorld();
            _service = new MaterialService(registry, _world, line => Plugin.Logger?.LogInfo(line), e => EventBus.Publish(e));
            _countGate = ServerCountGate;
            _compose = ServerCompose;
            _payGate = ServerPayGate;
            _pay = ServerPay;
        }
        else
        {
            _countGate = ClientCountGate;
            _compose = ClientCompose;
            _payGate = null;
            _pay = null;
        }

        _bound = BootstrapLink.TrySetMaterialSlots(_countGate, _compose, _payGate, _pay);
        _reason = _bound ? "" : "native seams not armed (" + bootstrap + ")";
        if (!_bound) return;

        if (_server)
        {
            // Run even when the system is skipped, so every Before has its After (a throwing system is
            // the one case without one: its scope is current only for that frame and closes at the next
            // governed Before or the next frame tick).
            var always = new HookOptions { OnlyWhenSystemRuns = false };
            Scope.Closed = CloseOperations;
            Hooks.Add(SystemHooks.Before<StartCraftingSystem>(ctx => Scope.Before(MaterialOperation.Craft, ctx.SystemName, FrameNow()), always));
            Hooks.Add(SystemHooks.After<StartCraftingSystem>(ctx => Scope.After(ctx.SystemName), always));
            Hooks.Add(SystemHooks.Before<PlaceTileModelSystem>(ctx => Scope.Before(MaterialOperation.Build, ctx.SystemName, FrameNow()), always));
            Hooks.Add(SystemHooks.After<PlaceTileModelSystem>(ctx => Scope.After(ctx.SystemName), always));
            // Both repair events (equipped item, item at a workstation) are paid in this system's update.
            // Repair has no request filter: its scope honors seam calls only while RepairItemSystem itself
            // executes, so a stranded scope (the system threw before its After) never routes another
            // system's payment (forge, research, stables, coffin) as a Repair. The same for Upgrade.
            Hooks.Add(SystemHooks.Before<RepairItemSystem>(ctx => Scope.Before(MaterialOperation.Repair, ctx.SystemName, FrameNow(),
                OpenerKey<RepairItemSystem>(ctx.World, ref _repairKey, ref _repairKeyWorld)), always));
            Hooks.Add(SystemHooks.After<RepairItemSystem>(ctx => Scope.After(ctx.SystemName), always));
            // Heart upgrades, claims, raids and destroys are all paid in this update; UpgradeFilter keeps
            // only the calls of an upgrade request governed.
            Hooks.Add(SystemHooks.Before<ProjectM.Gameplay.Systems.CastleHeartEventSystem>(ctx => Scope.Before(MaterialOperation.Upgrade, ctx.SystemName, FrameNow(),
                OpenerKey<ProjectM.Gameplay.Systems.CastleHeartEventSystem>(ctx.World, ref _heartKey, ref _heartKeyWorld)), always));
            Hooks.Add(SystemHooks.After<ProjectM.Gameplay.Systems.CastleHeartEventSystem>(ctx => Scope.After(ctx.SystemName), always));
            Publisher.ClearEveryoneOnce();
            _disconnected = EventBus.On<PlayerDisconnected>(e => { Publisher.Disconnected(CastleId.From(e.UserEntity)); });
        }
        else
        {
            // Paired like the server's scopes; the flag is also stamped with its frame, so a throwing
            // update (no After) cannot leave it set.
            var always = new HookOptions { OnlyWhenSystemRuns = false };
            Hooks.Add(SystemHooks.Before<ProjectM.CastleBuilding.Systems.BuildModeSystem>(hook =>
            {
                _inBuildMode = true;
                _buildModeFrame = FrameNow();
                MaterialPlacementDispatch.Enter(Overlay.TryGet(MaterialOperation.Build, out _));
            }, always));
            Hooks.Add(SystemHooks.After<ProjectM.CastleBuilding.Systems.BuildModeSystem>(_ =>
            {
                _inBuildMode = false;
                MaterialPlacementDispatch.Restore();
            }, always));
            CustomEvents.OnReceive(MaterialViewWire.EventId, OnView);
            _clientHandler = true;
            Handshake.ClientConnectionEnded += OnConnectionEnded;
        }
        Plugin.Frame += Frame;
        _frameHooked = true;
        Plugin.Logger?.LogInfo("materials: Core seam handlers bound (" + (_server ? "server: availability + payment" : "client: availability") + ")");
    }

    internal static void Uninstall()
    {
        if (!_server) MaterialPlacementDispatch.Restore();
        if (_bound) BootstrapLink.TrySetMaterialSlots(null, null, null, null);
        _bound = false;
        _reason = "Core uninstalled";
        foreach (var h in Hooks) { try { h.Dispose(); } catch { /* best effort */ } }
        Hooks.Clear();
        _disconnected?.Dispose();
        _disconnected = null;
        if (_frameHooked) Plugin.Frame -= Frame;
        _frameHooked = false;
        if (_clientHandler)
        {
            CustomEvents.RemoveHandler(MaterialViewWire.EventId, OnView);
            Handshake.ClientConnectionEnded -= OnConnectionEnded;
            _clientHandler = false;
        }
        try { _service?.EndScope(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("materials: closing open operations threw: " + e.Message); }
        _countGate = null; _compose = null; _payGate = null; _pay = null;
        Scope.Clear();
        Scope.Closed = null;
        UpgradeFilter.Clear();
        Publisher.Clear();
        Overlay.Clear();
        Dirty.Clear();
        _inBuildMode = false;
        _buildMenuFrame = -1;
        _service = null;
        _world = null;
        _registry = null;
        Reads = null;
    }

    internal static void OnRegistrationChanged()
    {
        // Keep recipients so a removed provider can send their clearing packets.
        Refresh.ForceAll();
    }

    // ---- scope -----------------------------------------------------------------------------------

    private static long FrameNow() => UnityEngine.Time.frameCount;

    /// <summary>A governed system finished (or its scope was stranded): close its operations and publish what they paid.</summary>
    private static void CloseOperations()
    {
        try { _service?.EndScope(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("materials: closing operations threw: " + e.Message); }
    }

    private static bool TryOperation(ulong target, ulong heart, out MaterialOperation op)
    {
        op = default;
        if (_service == null || !Dev.MainThread.IsMainThread) return false;
        long frame = FrameNow();
        ulong opener = Scope.Opener;
        ulong executing = opener != 0 ? ExecutingSystemKey() : 0;
        if (!Scope.TryCurrent(frame, executing, out op, out long token))
        {
            if (opener != 0 && executing != opener && !_foreignCallLogged && Scope.TryCurrent(frame, opener, out var stranded, out _))
            {
                _foreignCallLogged = true;
                Plugin.Logger?.LogWarning("materials: a seam call inside the " + stranded + " scope came from another system (its opener threw before its After, or the executing system is unreadable); that call keeps the game's own rules");
            }
            return false;
        }
        if (op == MaterialOperation.Upgrade && !IsUpgradeCall(token, target, heart)) return false;
        _service.BeginScope(token);
        return true;
    }

    /// <summary>
    /// The opening system's key for a scope, cached per world (a lookup only after a world change).
    /// <see cref="MaterialSystemKey.Unreadable"/> when the system or its handle cannot be read: that
    /// scope then refuses every seam call rather than falling back to the frame rule.
    /// </summary>
    private static ulong OpenerKey<TSystem>(World world, ref ulong key, ref ulong keyWorld) where TSystem : ComponentSystemBase
    {
        try
        {
            ulong seq = world.SequenceNumber;
            if (key != 0 && keyWorld == seq) return key;
            var system = world.GetExistingSystemManaged<TSystem>();
            if (system == null) return MaterialSystemKey.Unreadable;
            var h = system.SystemHandle;
            key = MaterialSystemKey.Pack(h.m_Handle, h.m_Version, h.m_WorldSeqNo);
            keyWorld = seq;
            return key;
        }
        catch (Exception e)
        {
            if (!_foreignCallLogged)
            {
                _foreignCallLogged = true;
                Plugin.Logger?.LogWarning("materials: the " + typeof(TSystem).Name + " handle is unreadable; its scope keeps the game's own rules: " + e.Message);
            }
            return MaterialSystemKey.Unreadable;
        }
    }

    /// <summary>The system the server world is executing now (<c>WorldUnmanaged.ExecutingSystem</c>), or 0 when unreadable.</summary>
    private static ulong ExecutingSystemKey()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return 0;
            var h = world.Unmanaged.ExecutingSystem;
            return MaterialSystemKey.Pack(h.m_Handle, h.m_Version, h.m_WorldSeqNo);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Inside the castle heart system: is this count or removal the cost of an upgrade request (and not
    /// of a claim, raid or destroy request)? The requests are read once per scope from the event
    /// entities the system is handling; they are destroyed only after every request was processed.
    /// </summary>
    private static bool IsUpgradeCall(long token, ulong target, ulong heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || heart == 0) return false;
        ulong heartNet = MaterialNet.Of(world.EntityManager, Ent(heart));
        return UpgradeFilter.IsUpgrade(token, target, heartNet, LoadHeartRequests);
    }

    private static void LoadHeartRequests(List<MaterialHeartRequest> into)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        var events = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<CastleHeartInteractEvent>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadOnly));
        foreach (var e in events)
        {
            CastleHeartInteractEvent request;
            FromCharacter from;
            try
            {
                if (!e.TryRead(em, out request) || !e.TryRead(em, out from)) continue;
            }
            catch (Exception ex)
            {
                // A layout mismatch: no call is classified as an upgrade, so upgrades stay vanilla.
                if (!_heartRequestLogged)
                {
                    _heartRequestLogged = true;
                    Plugin.Logger?.LogWarning("materials: castle heart requests unreadable; upgrades keep the game's own rules: " + ex.Message);
                }
                return;
            }
            var id = request.CastleHeart;
            if (id.Type != NetworkIdType.Normal) continue;
            ulong heartNet = ((ulong)(uint)id.Normal_Index << 32) | (uint)id.Normal_Generation;
            if (heartNet == 0) heartNet = ulong.MaxValue; // MaterialNet.Of's packing
            ulong character = ((ulong)(uint)from.Character.Version << 32) | (uint)from.Character.Index;
            into.Add(new MaterialHeartRequest(character, heartNet, request.EventType == CastleHeartInteractEventType.Upgrade));
        }
    }

    private static CastleId Id(ulong packed) => new((int)(packed & 0xFFFFFFFF), (int)(packed >> 32));

    private static Entity Ent(ulong packed) => new() { Index = (int)(packed & 0xFFFFFFFF), Version = (int)(packed >> 32) };

    // ---- server handlers ---------------------------------------------------------------------------

    private static bool ServerCountGate(ulong target, ulong heart)
    {
        if (_world == null || !TryOperation(target, heart, out var op)) return false;
        _world.HeartHint = Ent(heart);
        try { return _service!.CountGate(Id(target), op); }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static int ServerCompose(ulong target, ulong heart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity)
    {
        if (_world == null || !TryOperation(target, heart, out var op)) return -1;
        _world.HeartHint = Ent(heart);
        try
        {
            MaterialRows.Sum(new ReadOnlySpan<byte>((void*)fullRows, MaterialRows.Bytes(fullLength)), fullLength, FullScratch);
            MaterialRows.Sum(new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, BagScratch);
            if (!_service!.ComposeExtraRows(Id(target), op, FullScratch, BagScratch, ExtraScratch)) return -1;
            int need = MaterialRows.Needed(bagLength, ExtraScratch.Count);
            if (need > capacity) return need;
            int written = MaterialRows.Compose(new Span<byte>((void*)outRows, MaterialRows.Bytes(capacity)),
                new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, ExtraScratch);
            // The composed rows fit and go to the game's check: only now does the payment follow the quotes.
            _service.MarkChecked(Id(target), op);
            if (_traceNextCheck)
            {
                _traceNextCheck = false;
                Plugin.Logger?.LogInfo(MaterialTrace.Describe(op, Id(target),
                    new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, ExtraScratch,
                    new ReadOnlySpan<byte>((void*)outRows, MaterialRows.Bytes(written)), written));
            }
            return written;
        }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static bool ServerPayGate(ulong target, ulong heart, int item)
    {
        if (_world == null || !TryOperation(target, heart, out var op)) return false;
        _world.HeartHint = Ent(heart);
        try { return _service!.GovernsLine(Id(target), op, item); }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static int ServerPay(ulong target, ulong heart, int item, int amount, bool destroyItem, out ulong itemEntity)
    {
        // Pool payments come only from slots without an item entity, so there is never one to return;
        // the bag's own item entity (if any) was already written by the game's removal.
        itemEntity = 0;
        if (_world == null || !TryOperation(target, heart, out var op)) return amount;
        _world.HeartHint = Ent(heart);
        try
        {
            var r = _service!.Pay(Id(target), op, item, amount);
            if (_world.TryContext(Id(target), op, out var ctx)) Dirty.Add(ctx.User);
            return r.Remainder;
        }
        finally { _world.HeartHint = Entity.Null; }
    }

    // ---- server views ------------------------------------------------------------------------------

    private static void Frame()
    {
        if (!_server) return;
        var registry = _registry;
        if (registry == null || _service == null) return;
        // A scope whose system threw before its closing hook: close its operations now.
        Scope.Tick(FrameNow());
        if (Scope.OpenSystem == null && (_service.OpenSessions > 0 || _service.PendingEvents > 0)) CloseOperations();
        registry.DrainInvalidations(Drain, out bool all);
        foreach (var u in Drain) Dirty.Add(u);
        // Every view stale (InvalidateAll, a provider registered or went away): recompute everyone.
        if (all) _service.ForgetViews();
        if (!Refresh.Tick(Environment.TickCount64, all, Dirty.Count > 0, out bool everyone, out bool force)) return;
        try
        {
            if (PushViews(everyone, force) && everyone) Publisher.FirstRefreshDone();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("materials: view refresh threw: " + e.Message); }
        finally { Reads?.Clear(); }
        Dirty.Clear();
    }

    /// <summary>The refresh's read cache: each inventory read at most once per refresh, cleared after it.</summary>
    private static MaterialStockReads? Reads;

    /// <summary>
    /// Refresh and send; false when the world was not ready (nothing was sent). A player's view is
    /// recomputed only when it is new, the player is dirty (an invalidation or a governed payment), or
    /// something it depends on changed (<see cref="MaterialService.RefreshView"/>); otherwise the cached
    /// view is reused, and a forced resend sends it again unchanged.
    /// </summary>
    private static bool PushViews(bool everyone, bool force)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || _service == null || _world == null) return false;
        var em = world.EntityManager;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return false;
        if (Reads == null) Reads = new MaterialStockReads(_world);
        Reads.Clear();
        if (everyone) _service.BeginFullRefresh();
        foreach (var p in PlayerDirectory.Online(world))
        {
            var s = p.Session;
            if (!s.HasCharacter) continue;
            var user = new CastleId(s.UserIndex, s.UserVersion);
            bool dirty = Dirty.Contains(user);
            if (!everyone && !dirty) continue;
            var character = new CastleId(s.CharacterIndex, s.CharacterVersion);
            // Dirty is cleared after this refresh: drop every cached view of the character now, so a
            // view this refresh skips (Craft with no workstation open, Upgrade with no heart window)
            // is recomputed when it is next refreshed instead of reused.
            if (dirty) _service.ForgetViews(character);
            var userEntity = new Entity { Index = s.UserIndex, Version = s.UserVersion };
            int userIndex;
            try { userIndex = em.GetComponentData<User>(userEntity).Index; }
            catch { continue; }
            foreach (var op in MaterialClientPolicy.ViewOperations)
                Push(bootstrap, userIndex, user, character, op, force || dirty, dirty, Reads);
        }
        return true;
    }

    private static void Push(ServerBootstrapSystem bootstrap, int userIndex, CastleId user, CastleId character, MaterialOperation op, bool force,
        bool recompute, MaterialStockReads reads)
    {
        MaterialViewContent? content = null;
        // A Craft view only means something at a workstation and an Upgrade view only with a castle
        // heart's window open; skip the provider calls otherwise. A Repair view is not sent (see
        // MaterialClientPolicy.ViewOperations).
        ulong stationNet = op switch
        {
            MaterialOperation.Craft => _world!.StationNet(character),
            MaterialOperation.Upgrade => _world!.HeartTargetNet(character),
            _ => 0,
        };
        // Upgrade: only the window of a heart the player or the player's clan owns.
        if (op == MaterialOperation.Upgrade && stationNet != 0
            && !MaterialClientPolicy.MayViewUpgrade(_world!.OwnershipOf(user, stationNet, character)))
            stationNet = 0;
        if ((!MaterialClientPolicy.NeedsStation(op) || stationNet != 0)
            && _service!.RefreshView(character, op, reads, recompute, out var result) != MaterialViewOutcome.NotGoverned)
        {
            var ctx = result.Context;
            content = new MaterialViewContent(ctx.StandingTerritory, result.Revision, _world!.HeartNet(ctx.StandingTerritory), stationNet, result.Rows);
        }
        var view = Publisher.Next(user, op, Progenitor.WorldState.WorldEpoch.Current, content, force);
        if (view == null) return;
        bool delivered = false;
        try { delivered = CustomEvents.SendToUser(bootstrap, userIndex, MaterialViewWire.EventId, MaterialViewWire.Encode(view)); }
        finally
        {
            Publisher.Sent(user, op, delivered);
            if (delivered) _registry?.CountView();
        }
    }

    // ---- client ------------------------------------------------------------------------------------

    private static void OnView(int eventId, byte* payload, int length)
    {
        if (!MaterialViewWire.TryDecode(new ReadOnlySpan<byte>(payload, length), out var view, out string why))
        {
            Plugin.Logger?.LogWarning("materials: dropped a malformed totals packet: " + why);
            return;
        }
        Overlay.Apply(view!);
    }

    private static void OnConnectionEnded() => Overlay.Clear();

    private static bool BuildContext(World world)
    {
        if (_inBuildMode && _buildModeFrame == FrameNow()) return true;
        long frame = FrameNow();
        if (frame != _buildMenuFrame)
        {
            _buildMenuFrame = frame;
            _buildMenuShown = Ui.BuildMenuSearch.IsBuildMenuShown(world);
        }
        return _buildMenuShown;
    }

    private static bool TryClientSelect(ulong target, ulong heart, out IReadOnlyDictionary<int, int> totals)
    {
        totals = null!;
        if (!Dev.MainThread.IsMainThread || Overlay.Count == 0) return false;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return false;
        var em = world.EntityManager;
        var character = Ent(target);
        if (!character.Exists(em) || !character.Has<LocalCharacter>(em)) return false;
        var station = UnityMaterialWorld.StationOf(em, character);
        var heartTarget = station == Entity.Null ? UnityMaterialWorld.HeartTargetOf(em, character) : Entity.Null;
        var call = MaterialClientPolicy.Call(true, MaterialNet.Of(em, Ent(heart)), BuildContext(world),
            station == Entity.Null ? 0 : MaterialNet.Of(em, station),
            heartTarget == Entity.Null ? 0 : MaterialNet.Of(em, heartTarget));
        return MaterialClientPolicy.TrySelect(Overlay, call, out _, out totals);
    }

    private static bool ClientCountGate(ulong target, ulong heart) => TryClientSelect(target, heart, out _);

    private static int ClientCompose(ulong target, ulong heart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity)
    {
        if (!TryClientSelect(target, heart, out var totals)) return -1;
        MaterialRows.Sum(new ReadOnlySpan<byte>((void*)fullRows, MaterialRows.Bytes(fullLength)), fullLength, FullScratch);
        MaterialRows.Sum(new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, BagScratch);
        MaterialClientPolicy.ComposeExtra(FullScratch, BagScratch, totals, ExtraScratch);
        int need = MaterialRows.Needed(bagLength, ExtraScratch.Count);
        if (need > capacity) return need;
        return MaterialRows.Compose(new Span<byte>((void*)outRows, MaterialRows.Bytes(capacity)),
            new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, ExtraScratch);
    }
}
#endif
