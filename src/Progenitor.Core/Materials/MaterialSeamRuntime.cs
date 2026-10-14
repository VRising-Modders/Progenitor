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
/// <para><b>Server.</b> A scope is open while <c>StartCraftingSystem</c> (Craft) or
/// <c>PlaceTileModelSystem</c> (Build) updates; only seam calls inside a scope are routed to the
/// provider. Both systems check affordability and pay in the same synchronous call on the main thread,
/// so one game operation is one <see cref="MaterialService"/> session: the provider is asked once at the
/// check, every line is paid from that answer, and the queued <see cref="MaterialPaid"/> events are
/// published when the system's update ends. Every second (and right after an invalidation or a governed
/// payment) each online player's Build and Craft totals are recomputed and sent when they changed
/// (<see cref="MaterialRefreshPolicy"/>, <see cref="MaterialViewPublisher"/>).</para>
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
        foreach (var op in new[] { MaterialOperation.Build, MaterialOperation.Craft })
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
        Publisher.Clear();
        Overlay.Clear();
        Dirty.Clear();
        _inBuildMode = false;
        _buildMenuFrame = -1;
        _service = null;
        _world = null;
        _registry = null;
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

    private static bool TryOperation(out MaterialOperation op)
    {
        op = default;
        if (_service == null || !Dev.MainThread.IsMainThread || !Scope.TryCurrent(FrameNow(), out op, out long token)) return false;
        _service.BeginScope(token);
        return true;
    }

    private static CastleId Id(ulong packed) => new((int)(packed & 0xFFFFFFFF), (int)(packed >> 32));

    private static Entity Ent(ulong packed) => new() { Index = (int)(packed & 0xFFFFFFFF), Version = (int)(packed >> 32) };

    // ---- server handlers ---------------------------------------------------------------------------

    private static bool ServerCountGate(ulong target, ulong heart)
    {
        if (_world == null || !TryOperation(out var op)) return false;
        _world.HeartHint = Ent(heart);
        try { return _service!.CountGate(Id(target), op); }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static int ServerCompose(ulong target, ulong heart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity)
    {
        if (_world == null || !TryOperation(out var op)) return -1;
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
            return written;
        }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static bool ServerPayGate(ulong target, ulong heart, int item)
    {
        if (_world == null || !TryOperation(out var op)) return false;
        _world.HeartHint = Ent(heart);
        try { return _service!.GovernsLine(Id(target), op, item); }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static int ServerPay(ulong target, ulong heart, int item, int amount, bool destroyItem, out ulong itemEntity)
    {
        // Pool payments come only from slots without an item entity, so there is never one to return;
        // the bag's own item entity (if any) was already written by the game's removal.
        itemEntity = 0;
        if (_world == null || !TryOperation(out var op)) return amount;
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
        if (!Refresh.Tick(Environment.TickCount64, all, Dirty.Count > 0, out bool everyone, out bool force)) return;
        try
        {
            if (PushViews(everyone, force) && everyone) Publisher.FirstRefreshDone();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("materials: view refresh threw: " + e.Message); }
        Dirty.Clear();
    }

    private static readonly Dictionary<int, int> TotalsScratch = new();

    /// <summary>Recompute and send; false when the world was not ready (nothing was sent).</summary>
    private static bool PushViews(bool everyone, bool force)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || _service == null || _world == null) return false;
        var em = world.EntityManager;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return false;
        foreach (var p in PlayerDirectory.Online(world))
        {
            var s = p.Session;
            if (!s.HasCharacter) continue;
            var user = new CastleId(s.UserIndex, s.UserVersion);
            bool dirty = Dirty.Contains(user);
            if (!everyone && !dirty) continue;
            var character = new CastleId(s.CharacterIndex, s.CharacterVersion);
            var userEntity = new Entity { Index = s.UserIndex, Version = s.UserVersion };
            int userIndex;
            try { userIndex = em.GetComponentData<User>(userEntity).Index; }
            catch { continue; }
            Push(bootstrap, userIndex, user, character, MaterialOperation.Build, force || dirty);
            Push(bootstrap, userIndex, user, character, MaterialOperation.Craft, force || dirty);
        }
        return true;
    }

    private static void Push(ServerBootstrapSystem bootstrap, int userIndex, CastleId user, CastleId character, MaterialOperation op, bool force)
    {
        MaterialViewContent? content = null;
        // A Craft view only means something at a workstation; skip the provider calls otherwise.
        ulong stationNet = op == MaterialOperation.Craft ? _world!.StationNet(character) : 0;
        if ((op != MaterialOperation.Craft || stationNet != 0)
            && _service!.TryView(character, op, TotalsScratch, out var ctx, out long revision))
        {
            var rows = new List<MaterialLine>(TotalsScratch.Count);
            foreach (var kv in TotalsScratch) rows.Add(new MaterialLine(kv.Key, kv.Value));
            rows.Sort((a, b) => a.Item.CompareTo(b.Item));
            content = new MaterialViewContent(ctx.StandingTerritory, revision, _world!.HeartNet(ctx.StandingTerritory), stationNet, rows);
        }
        var view = Publisher.Next(user, op, Progenitor.WorldState.WorldEpoch.Current, content, force);
        if (view == null) return;
        bool delivered = false;
        try { delivered = CustomEvents.SendToUser(bootstrap, userIndex, MaterialViewWire.EventId, MaterialViewWire.Encode(view)); }
        finally { Publisher.Sent(user, op, delivered); }
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
        var call = MaterialClientPolicy.Call(true, MaterialNet.Of(em, Ent(heart)), BuildContext(world),
            station == Entity.Null ? 0 : MaterialNet.Of(em, station));
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
