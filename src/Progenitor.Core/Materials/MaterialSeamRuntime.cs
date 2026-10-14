#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Networking;
using Progenitor.Players;
using Unity.Entities;

namespace Progenitor.Materials;

/// <summary>
/// Binds Core's availability and payment handlers to Bootstrap's opt-in material seams and runs the
/// per-player totals that the client's cost display reads.
///
/// <para><b>Server.</b> A scope is open while <c>StartCraftingSystem</c> (Craft) or
/// <c>PlaceTileModelSystem</c> (Build) updates; only seam calls inside a scope are routed to the
/// provider. Both systems check affordability and pay in the same synchronous call on the main thread
/// (the game's metadata shows the build path checks then charges inside one handler, and the craft
/// start checks every ingredient then removes each in the same body), so nothing here spans a frame or
/// holds a reservation. Every second (and right after an invalidation or a governed payment) each
/// online player's Build and Craft totals are recomputed and sent when they changed.</para>
///
/// <para><b>Client.</b> The received totals replace the shared-storage rows of the game's merged
/// count for the local character: bag rows from the game plus the server's rows. Without a received
/// view the game's own counts are shown. A displayed count is never authorization: the server
/// re-checks and pays at action time.</para>
/// </summary>
internal static unsafe class MaterialSeamRuntime
{
    // Shapes identical to Bootstrap's MaterialSeams delegates (re-bound there by CreateDelegate).
    private delegate bool CountGateFn(ulong target, ulong castleHeart);
    private delegate int CountComposeFn(ulong target, ulong castleHeart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity);
    private delegate bool PayGateFn(ulong target, ulong castleHeart, int itemType);
    private delegate int PayFn(ulong target, ulong castleHeart, int itemType, int amount, bool destroyItem, out ulong itemEntity);

    private const long ViewIntervalMs = 1_000;

    private static MaterialSourceRegistry? _registry;
    private static MaterialService? _service;
    private static UnityMaterialWorld? _world;
    private static readonly MaterialScope Scope = new();
    private static readonly MaterialViewBook Book = new();
    private static readonly MaterialOverlayStore Overlay = new();
    private static readonly List<IDisposable> Hooks = new();
    private static readonly Stack<(long Token, long Frame)> Open = new();
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
    private static long _nextView, _nextResync;
    private static bool _forceViews;
    private static IDisposable? _disconnected;

    internal static (bool installed, string reason) HookState() => (_bound, _bound ? "" : _reason);

    internal static Dictionary<string, object?> DescribeViews()
    {
        var result = new Dictionary<string, object?>();
        foreach (var op in new[] { MaterialOperation.Build, MaterialOperation.Craft })
            result[op.ToString()] = Overlay.TryGet(op, out var totals) ? new Dictionary<int, int>(totals) : null;
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
            Hooks.Add(SystemHooks.Before<StartCraftingSystem>(_ => Enter(MaterialOperation.Craft)));
            Hooks.Add(SystemHooks.After<StartCraftingSystem>(_ => Exit()));
            Hooks.Add(SystemHooks.Before<PlaceTileModelSystem>(_ => Enter(MaterialOperation.Build)));
            Hooks.Add(SystemHooks.After<PlaceTileModelSystem>(_ => Exit()));
            _disconnected = EventBus.On<PlayerDisconnected>(e => { Book.Forget(CastleId.From(e.UserEntity)); });
        }
        else
        {
            Hooks.Add(SystemHooks.Before<ProjectM.CastleBuilding.Systems.BuildModeSystem>(hook =>
                MaterialPlacementDispatch.Enter(Overlay.TryGet(MaterialOperation.Build, out _))));
            Hooks.Add(SystemHooks.After<ProjectM.CastleBuilding.Systems.BuildModeSystem>(_ => MaterialPlacementDispatch.Restore()));
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
        _countGate = null; _compose = null; _payGate = null; _pay = null;
        Scope.Clear();
        Open.Clear();
        Book.Clear();
        Overlay.Clear();
        Dirty.Clear();
        _service = null;
        _world = null;
        _registry = null;
    }

    internal static void OnRegistrationChanged()
    {
        // Keep recipients so a removed provider can send their clearing packets.
        _forceViews = true;
        _nextView = 0;
    }

    // ---- scope -----------------------------------------------------------------------------------

    private static long FrameNow() => UnityEngine.Time.frameCount;

    private static void Enter(MaterialOperation op)
    {
        long frame = FrameNow();
        while (Open.Count > 0 && Open.Peek().Frame != frame) Open.Pop();
        Open.Push((Scope.Enter(op, frame), frame));
    }

    private static void Exit()
    {
        if (Open.Count == 0) return;
        Scope.Exit(Open.Pop().Token);
    }

    private static bool TryOperation(out MaterialOperation op)
    {
        op = default;
        return Dev.MainThread.IsMainThread && Scope.TryCurrent(FrameNow(), out op);
    }

    private static CastleId Id(ulong packed) => new((int)(packed & 0xFFFFFFFF), (int)(packed >> 32));

    private static Entity Ent(ulong packed) => new() { Index = (int)(packed & 0xFFFFFFFF), Version = (int)(packed >> 32) };

    // ---- server handlers ---------------------------------------------------------------------------

    private static bool ServerCountGate(ulong target, ulong heart)
    {
        if (_service == null || _world == null || !TryOperation(out var op)) return false;
        _world.HeartHint = Ent(heart);
        try { return _service.TryGoverned(Id(target), op, out _, out _, out _); }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static int ServerCompose(ulong target, ulong heart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity)
    {
        if (_service == null || _world == null || !TryOperation(out var op)) return -1;
        _world.HeartHint = Ent(heart);
        try
        {
            MaterialRows.Sum(new ReadOnlySpan<byte>((void*)fullRows, MaterialRows.Bytes(fullLength)), fullLength, FullScratch);
            MaterialRows.Sum(new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, BagScratch);
            if (!_service.ComposeExtraRows(Id(target), op, FullScratch, BagScratch, ExtraScratch)) return -1;
            int need = MaterialRows.Needed(bagLength, ExtraScratch.Count);
            if (need > capacity) return need;
            return MaterialRows.Compose(new Span<byte>((void*)outRows, MaterialRows.Bytes(capacity)),
                new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, ExtraScratch);
        }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static bool ServerPayGate(ulong target, ulong heart, int item)
    {
        if (_service == null || _world == null || !TryOperation(out var op)) return false;
        _world.HeartHint = Ent(heart);
        try { return _service.GovernsLine(Id(target), op, item); }
        finally { _world.HeartHint = Entity.Null; }
    }

    private static int ServerPay(ulong target, ulong heart, int item, int amount, bool destroyItem, out ulong itemEntity)
    {
        // Pool payments come only from slots without an item entity, so there is never one to return;
        // the bag's own item entity (if any) was already written by the game's removal.
        itemEntity = 0;
        if (_service == null || _world == null || !TryOperation(out var op)) return amount;
        _world.HeartHint = Ent(heart);
        try
        {
            var r = _service.Pay(Id(target), op, item, amount);
            if (r.Paid > 0 && _world.TryContext(Id(target), op, out var ctx)) Dirty.Add(ctx.User);
            return r.Remainder;
        }
        finally { _world.HeartHint = Entity.Null; }
    }

    // ---- server views ------------------------------------------------------------------------------

    private static void Frame()
    {
        if (!_server) return;
        long now = Environment.TickCount64;
        var registry = _registry;
        if (registry == null || _service == null) return;
        registry.DrainInvalidations(Drain, out bool all);
        if (all) _forceViews = true;
        foreach (var u in Drain) Dirty.Add(u);
        if (!all && Dirty.Count == 0 && now < _nextView) return;
        bool full = all || now >= _nextView;
        if (full) _nextView = now + ViewIntervalMs;
        bool force = _forceViews || now >= _nextResync;
        if (force) { _nextResync = now + 10_000; _forceViews = false; }
        try { PushViews(full || force, force); }
        catch (Exception e) { Plugin.Logger?.LogWarning("materials: view refresh threw: " + e.Message); }
        Dirty.Clear();
    }

    private static readonly Dictionary<int, int> TotalsScratch = new();
    private static readonly List<MaterialLine> RowsScratch = new();

    private static void PushViews(bool everyone, bool force)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || _service == null) return;
        var em = world.EntityManager;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        foreach (var p in PlayerDirectory.Online(world))
        {
            var s = p.Session;
            if (!s.HasCharacter) continue;
            var user = new CastleId(s.UserIndex, s.UserVersion);
            if (!everyone && !Dirty.Contains(user)) continue;
            var character = new CastleId(s.CharacterIndex, s.CharacterVersion);
            var userEntity = new Entity { Index = s.UserIndex, Version = s.UserVersion };
            int userIndex;
            try { userIndex = em.GetComponentData<User>(userEntity).Index; }
            catch { continue; }
            Push(bootstrap, userIndex, user, character, MaterialOperation.Build, force || Dirty.Contains(user));
            Push(bootstrap, userIndex, user, character, MaterialOperation.Craft, force || Dirty.Contains(user));
        }
    }

    private static void Push(ServerBootstrapSystem bootstrap, int userIndex, CastleId user, CastleId character, MaterialOperation op, bool force)
    {
        MaterialView view;
        // A Craft view only means something at a workstation; skip the provider calls otherwise.
        bool atStation = op != MaterialOperation.Craft
            || (_world!.TryContext(character, op, out var probe) && !probe.Station.IsNone);
        if (atStation && _service!.TryView(character, op, TotalsScratch, out var ctx, out long revision))
        {
            RowsScratch.Clear();
            foreach (var kv in TotalsScratch) RowsScratch.Add(new MaterialLine(kv.Key, kv.Value));
            RowsScratch.Sort((a, b) => a.Item.CompareTo(b.Item));
            ulong fp = MaterialViewBook.Fingerprint(ctx.StandingTerritory, revision, RowsScratch);
            if (!Book.ShouldSend(user, op, fp, force, out uint seq)) return;
            view = new MaterialView(op, ctx.WorldEpoch, revision, seq, ctx.StandingTerritory, false, false, RowsScratch.ToArray());
        }
        else
        {
            if (!Book.HasView(user, op)) return;
            if (!Book.ShouldSend(user, op, MaterialViewBook.ClearedFingerprint, false, out uint seq)) return;
            view = new MaterialView(op, Progenitor.WorldState.WorldEpoch.Current, 0, seq, -1, true, false, Array.Empty<MaterialLine>());
        }
        var bytes = MaterialViewWire.Encode(view);
        try
        {
            if (!CustomEvents.SendToUser(bootstrap, userIndex, MaterialViewWire.EventId, bytes)) Book.Retry(user, op);
        }
        catch { Book.Retry(user, op); throw; }
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

    private static MaterialOperation ClientOperation(ulong target)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return MaterialOperation.Build;
        return UnityMaterialWorld.StationOf(world.EntityManager, Ent(target)) != Entity.Null
            ? MaterialOperation.Craft
            : MaterialOperation.Build;
    }

    private static bool ClientCountGate(ulong target, ulong heart)
    {
        if (!Dev.MainThread.IsMainThread || Overlay.Count == 0) return false;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated || !Ent(target).Has<PlayerCharacter>(world.EntityManager)) return false;
        return Overlay.TryGet(ClientOperation(target), out _);
    }



    private static int ClientCompose(ulong target, ulong heart, IntPtr fullRows, int fullLength, IntPtr bagRows, int bagLength, IntPtr outRows, int capacity)
    {
        if (!Overlay.TryGet(ClientOperation(target), out var totals)) return -1;
        ExtraScratch.Clear();
        foreach (var kv in totals) if (kv.Value > 0) ExtraScratch.Add(new MaterialLine(kv.Key, kv.Value));
        int need = MaterialRows.Needed(bagLength, ExtraScratch.Count);
        if (need > capacity) return need;
        return MaterialRows.Compose(new Span<byte>((void*)outRows, MaterialRows.Bytes(capacity)),
            new ReadOnlySpan<byte>((void*)bagRows, MaterialRows.Bytes(bagLength)), bagLength, ExtraScratch);
    }
}
#endif
