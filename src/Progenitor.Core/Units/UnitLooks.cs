#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Networking;
using ProjectM;
using ProjectM.Hybrid;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.SetLook</c> / <c>GetLook</c> / <c>ClearLook</c> (docs/modules.md, "Unit
/// look"). Both sides, main thread only.
///
/// <para><b>Server.</b> Records the wanted look per entity (<see cref="UnitLookServerTable"/>): a unit
/// this plugin spawned (by handle) or any networked entity such as a player character (by entity),
/// waits for the unit's <c>NetworkId</c> (the game assigns it a spawn system or two after the spawn
/// tick), then sends <see cref="UnitLookMessage"/> on <see cref="UnitLookWire.EventId"/> to every
/// connected user. A joiner gets <c>ClearAll</c> and the full set on <see cref="PlayerConnected"/>.
/// A pass every <see cref="PassSeconds"/> s resolves pending ids, sends dirty entries and, when a unit
/// is gone, sends <c>Clear</c> and forgets it. Nothing is sent while nothing is tracked.</para>
///
/// <para><b>Client.</b> Holds the looks the server sent, keyed by network id
/// (<see cref="UnitLookClientTable"/>). A pass every <see cref="PassSeconds"/> s maps each tracked id
/// to its client entity (one query over <c>NetworkId</c> + <c>HybridModelUser</c>), follows
/// <c>HybridModelUser.HybridEntity</c> to the model's <c>UnityEngine.Transform</c>, records the model's
/// natural scale on first sight (<see cref="NaturalScaleBook"/>), writes <c>localScale</c> when it
/// drifted, and rewrites the <c>_BaseColor</c> / <c>_BlinkColor</c> shader properties through the
/// renderer's own <c>MaterialPropertyBlock</c> (read, set ours, write back: other properties the game
/// set are kept). A new model instance (streaming, re-entering view) restores the old one and starts
/// over. The pass costs two integer compares while nothing is tracked.</para>
///
/// <para><b>Lifetime.</b> One <see cref="Plugin.Frame"/> handler, one <c>CustomEvents</c> client
/// handler (client) or one <c>EventBus</c> subscription (server), all released by
/// <see cref="Uninstall"/>; the client restores every live model to vanilla there. Tables hold ids,
/// plain values and interop objects, never a plugin delegate.</para>
/// </summary>
internal static unsafe class UnitLooks
{
    /// <summary>Seconds between passes on either side.</summary>
    internal const double PassSeconds = 0.25;

    private const string BaseColorProperty = "_BaseColor";
    private const string OverlayProperty = "_BlinkColor";

    private static readonly UnitLookServerTable ServerTable = new();
    private static readonly UnitLookClientTable ClientTable = new();
    private static readonly NaturalScaleBook Book = new();
    private static readonly List<UnitLookClientTable.Entry> RestoreQueue = new();

    private static bool _installed;
    private static bool _server;
    private static bool _frameHooked;
    private static bool _clientHandler;
    private static IDisposable? _connectedSub;
    private static IDisposable? _disconnectedSub;
    private static long _lastPassMs;
    private static bool _passNow;
    private static int _decodeWarnings;

    private static UnityEngine.MaterialPropertyBlock? _block;
    private static int _baseId;
    private static int _overlayId;
    private static bool _propertyIds;

    // ---- install -------------------------------------------------------------------------------

    internal static void Install()
    {
        if (_installed) return;
        if (HarmonySlotHandoff.ShouldSkipModule(Units.ModuleName)) return;
        _server = Plugin.IsServerProcess;
        if (_server)
        {
            _connectedSub = EventBus.On<PlayerConnected>(OnPlayerConnected);
            _disconnectedSub = EventBus.On<PlayerDisconnected>(OnPlayerDisconnected);
        }
        else
        {
            CustomEvents.OnReceive(UnitLookWire.EventId, OnMessage);
            _clientHandler = true;
        }
        Plugin.Frame += Pass;
        _frameHooked = true;
        _installed = true;
    }

    internal static void Uninstall()
    {
        if (_frameHooked)
        {
            Plugin.Frame -= Pass;
            _frameHooked = false;
        }
        try { _connectedSub?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("UnitLooks: PlayerConnected dispose threw: " + e.Message); }
        _connectedSub = null;
        try { _disconnectedSub?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("UnitLooks: PlayerDisconnected dispose threw: " + e.Message); }
        _disconnectedSub = null;
        if (_clientHandler)
        {
            CustomEvents.RemoveHandler(UnitLookWire.EventId, OnMessage);
            _clientHandler = false;
        }

        if (_server)
        {
            // Best effort: tell clients to drop what this generation sent. A later generation has no
            // record of these looks and could never clear them.
            bool anySent = false;
            foreach (var entry in ServerTable.RemoveAll()) anySent |= entry.Sent;
            if (anySent) SendToAll(UnitLookWire.ClearAll());
        }
        else
        {
            int restored = 0;
            RestoreQueue.AddRange(ClientTable.RemoveAll());
            foreach (var entry in RestoreQueue)
                if (RestoreModel(entry)) restored++;
            RestoreQueue.Clear();
            if (restored > 0) Plugin.Logger?.LogInfo("UnitLooks: restored " + restored + " model(s) to vanilla on unload.");
        }

        Book.Clear();
        _block = null;
        _propertyIds = false;
        _installed = false;
        _passNow = false;
        _decodeWarnings = 0;
    }

    internal static string DescribePins()
    {
        var holds = new List<string>();
        if (_installed) holds.Add("installed");
        if (_frameHooked) holds.Add("Plugin.Frame");
        if (_connectedSub != null) holds.Add("EventBus.PlayerConnected");
        if (_disconnectedSub != null) holds.Add("EventBus.PlayerDisconnected");
        if (_clientHandler) holds.Add("CustomEvents.PruL");
        if (ServerTable.Count > 0) holds.Add("serverLooks=" + ServerTable.Count);
        if (ClientTable.Count > 0) holds.Add("clientLooks=" + ClientTable.Count);
        if (Book.Count > 0) holds.Add("models=" + Book.Count);
        return HarmonySlotHandoff.FormatModulePins(Units.ModuleName + ".Looks", holds);
    }

    // ---- server API ----------------------------------------------------------------------------

    /// <summary>Record and send <paramref name="look"/> for the unit. A vanilla look is a clear.</summary>
    internal static void Set(IUnitHandle handle, UnitLook look, string pluginId)
    {
        var (em, entity) = Resolve(handle, "SetLook");
        if (look.IsVanilla)
        {
            Clear(handle);
            return;
        }
        var entry = ServerTable.Set(handle.Entity, look, pluginId, WorldState.WorldEpoch.Current);
        Flush(em, entity, entry);
    }

    /// <summary>The look recorded for the unit, or a vanilla one.</summary>
    internal static UnitLook Get(IUnitHandle handle)
    {
        Resolve(handle, "GetLook");
        return ServerTable.Get(handle.Entity) ?? UnitLook.Vanilla;
    }

    /// <summary>Forget the unit's look and tell clients to restore its model.</summary>
    internal static void Clear(IUnitHandle handle)
    {
        Resolve(handle, "ClearLook");
        SendClearFor(ServerTable.Remove(handle.Entity));
    }

    /// <summary>
    /// Record and send <paramref name="look"/> for any networked entity, such as a player character. A
    /// vanilla look is a clear. Throws when the entity has no <c>NetworkId</c> or one whose type is not
    /// <c>Normal</c> (clients could never find it), or is a player character whose user is not connected.
    /// </summary>
    internal static void Set(Entity entity, UnitLook look, string pluginId)
    {
        var em = ResolveEntity(entity, "SetLook");
        if (look.IsVanilla)
        {
            Clear(entity);
            return;
        }
        if (!entity.TryRead(em, out NetworkId net))
            throw new InvalidOperationException("entity " + AiRuntime.Fmt(entity) + " has no NetworkId; a look is sent to clients by network id, so it cannot have one.");
        if (net.Type != NetworkIdType.Normal)
            throw new InvalidOperationException("entity " + AiRuntime.Fmt(entity) + " has a " + net.Type + " NetworkId; clients find a look by a Normal network id only.");
        var user = UnitId.None;
        if (entity.Has<PlayerCharacter>(em))
        {
            var userEntity = em.GetComponentData<PlayerCharacter>(entity).UserEntity;
            if (userEntity == Entity.Null || !em.Exists(userEntity) || !em.GetComponentData<User>(userEntity).IsConnected)
                throw new InvalidOperationException("player character " + AiRuntime.Fmt(entity) + " has no connected user; a player look is cleared when they disconnect.");
            user = UnitId.From(userEntity);
        }
        var entry = ServerTable.Set(UnitId.From(entity), look, pluginId, WorldState.WorldEpoch.Current, user);
        Flush(em, entity, entry);
    }

    /// <summary>The look recorded for <paramref name="entity"/>, or a vanilla one.</summary>
    internal static UnitLook Get(Entity entity)
    {
        ResolveEntity(entity, "GetLook");
        return ServerTable.Get(UnitId.From(entity)) ?? UnitLook.Vanilla;
    }

    /// <summary>Forget the look of <paramref name="entity"/> and tell clients to restore its model.</summary>
    internal static void Clear(Entity entity)
    {
        ResolveEntity(entity, "ClearLook");
        SendClearFor(ServerTable.Remove(UnitId.From(entity)));
    }

    /// <summary>Plugin unload: clear every look this plugin set whose unit is still there.</summary>
    internal static void ReleaseOwnedBy(string pluginId)
    {
        if (!_installed || !_server) return;
        foreach (var entry in ServerTable.RemoveOwnedBy(pluginId ?? ""))
            SendClearFor(entry);
    }

    private static void SendClearFor(UnitLookServerTable.Entry? entry)
    {
        if (entry == null || !entry.Sent || !entry.Key.IsValid) return;
        SendToAll(UnitLookWire.Clear(entry.Key));
    }

    /// <summary>Resolve the network id if needed and send the entry when it is dirty.</summary>
    private static void Flush(EntityManager em, Entity entity, UnitLookServerTable.Entry entry)
    {
        if (!entry.Key.IsValid && TryNetKey(em, entity, out var key)) entry.Key = key;
        if (!entry.Dirty || !entry.Key.IsValid) return;
        SendToAll(UnitLookWire.Set(entry.Key, entry.Look));
        entry.Dirty = false;
        entry.Sent = true;
    }

    private static bool TryNetKey(EntityManager em, Entity entity, out UnitNetKey key)
    {
        key = default;
        try
        {
            if (!entity.TryRead(em, out NetworkId net)) return false;
            if (net.Type != NetworkIdType.Normal) return false;
            key = new UnitNetKey(net.Normal_Index, net.Normal_Generation);
            return key.IsValid;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UnitLooks: NetworkId read of " + AiRuntime.Fmt(entity) + " threw: " + e.Message);
            return false;
        }
    }

    private static void SendToAll(UnitLookMessage message)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        try { CustomEvents.SendToAll(world, UnitLookWire.EventId, message); }
        catch (Exception e) { Plugin.Logger?.LogWarning("UnitLooks: send threw: " + e.Message); }
    }

    private static void OnPlayerConnected(PlayerConnected ev)
    {
        try
        {
            var entries = ServerTable.Snapshot();
            if (entries.Length == 0) return;
            var em = ev.World.EntityManager;
            CustomEvents.SendToUser(ev.World, ev.UserEntity, UnitLookWire.EventId, UnitLookWire.ClearAll());
            int sent = 0;
            foreach (var entry in entries)
            {
                // Pending entries go out to everyone, this joiner included, from the next pass.
                if (entry.Dirty || !entry.Key.IsValid) continue;
                if (CustomEvents.SendToUser(ev.World, ev.UserEntity, UnitLookWire.EventId, UnitLookWire.Set(entry.Key, entry.Look))) sent++;
            }
            Plugin.Logger?.LogInfo("UnitLooks: sent " + sent + " unit look(s) to " + ev.CharacterName + ".");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UnitLooks: join sync threw: " + e.Message);
        }
    }

    private static void OnPlayerDisconnected(PlayerDisconnected ev)
    {
        try
        {
            foreach (var entry in ServerTable.RemoveByUser(UnitId.From(ev.UserEntity)))
                SendClearFor(entry);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UnitLooks: disconnect clear threw: " + e.Message);
        }
    }

    /// <summary>False when <paramref name="entry"/> belongs to a player whose user is gone or offline.</summary>
    private static bool UserStillConnected(EntityManager em, UnitLookServerTable.Entry entry)
    {
        if (entry.CharacterUser.IsNone) return true;
        try
        {
            var user = entry.CharacterUser.ToEntity();
            return em.Exists(user) && em.GetComponentData<User>(user).IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private static EntityManager ResolveEntity(Entity entity, string op)
    {
        Units.EnsureMainThread(op);
        if (!_installed || !_server) throw new InvalidOperationException(op + " is server-only and unit looks are not installed here.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException(op + " is server-only and there is no server world.");
        var em = world.EntityManager;
        if (entity == Entity.Null || !em.Exists(entity)) throw new InvalidOperationException("entity " + AiRuntime.Fmt(entity) + " does not exist.");
        return em;
    }

    private static (EntityManager Em, Entity Entity) Resolve(IUnitHandle handle, string op)
    {
        Units.EnsureMainThread(op);
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (handle.IsDisposed) throw new InvalidOperationException("the unit handle is disposed.");
        if (!handle.Enabled) throw new InvalidOperationException("the unit handle is disabled.");
        if (!_installed || !_server) throw new InvalidOperationException(op + " is server-only and unit looks are not installed here.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException(op + " is server-only and there is no server world.");
        var em = world.EntityManager;
        var entity = handle.Entity.ToEntity();
        if (!em.Exists(entity)) throw new InvalidOperationException("unit " + handle.Entity + " no longer exists.");
        return (em, entity);
    }

    // ---- frame pass ----------------------------------------------------------------------------

    private static void Pass()
    {
        if (RestoreQueue.Count > 0) DrainRestores();
        bool empty = _server ? ServerTable.Count == 0 : ClientTable.Count == 0;
        if (empty) return;
        long now = Environment.TickCount64;
        if (!_passNow && now - _lastPassMs < (long)(PassSeconds * 1000)) return;
        _lastPassMs = now;
        _passNow = false;
        try
        {
            if (_server) ServerPass();
            else ClientPass();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UnitLooks: pass threw: " + e.Message);
        }
    }

    private static void ServerPass()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            ServerTable.RemoveAll();
            return;
        }
        var em = world.EntityManager;
        foreach (var entry in ServerTable.Snapshot())
        {
            var entity = entry.Unit.ToEntity();
            // A world reload re-numbers entities: the id may name another unit now.
            if (WorldState.WorldEpoch.IsStale(entry.Epoch) || !em.Exists(entity) || !UserStillConnected(em, entry))
            {
                SendClearFor(ServerTable.Remove(entry.Unit));
                continue;
            }
            Flush(em, entity, entry);
        }
    }

    // ---- client --------------------------------------------------------------------------------

    private static void OnMessage(int _, byte* payload, int length)
    {
        if (payload == null) return;
        if (!CustomEvents.TryRead<UnitLookMessage>(payload, length, out var message))
        {
            WarnDecode("a " + length + "-byte payload (expected " + sizeof(UnitLookMessage) + ")");
            return;
        }
        if (!UnitLookWire.TryDecode(message, out var op, out var key, out var look, out string? why))
        {
            WarnDecode(why ?? "an invalid message");
            return;
        }
        // Model writes wait for the next frame pass: this runs inside the network receive.
        switch (op)
        {
            case UnitLookOp.Set:
                ClientTable.Set(key, look!);
                _passNow = true;
                break;
            case UnitLookOp.Clear:
                if (ClientTable.Remove(key) is { } removed) RestoreQueue.Add(removed);
                break;
            case UnitLookOp.ClearAll:
                RestoreQueue.AddRange(ClientTable.RemoveAll());
                break;
        }
    }

    private static void WarnDecode(string what)
    {
        if (_decodeWarnings++ < 5)
            Plugin.Logger?.LogWarning("UnitLooks: ignored " + what + " on the unit-look channel.");
    }

    private static void DrainRestores()
    {
        foreach (var entry in RestoreQueue)
        {
            try { RestoreModel(entry); }
            catch (Exception e) { Plugin.Logger?.LogWarning("UnitLooks: restore threw: " + e.Message); }
        }
        RestoreQueue.Clear();
    }

    private static void ClientPass()
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
        {
            // Left the server: the models went with the world. Drop everything; a new session gets
            // ClearAll and its own set from the server on join.
            RestoreQueue.AddRange(ClientTable.RemoveAll());
            DrainRestores();
            Book.Clear();
            return;
        }

        var em = world.EntityManager;
        var byKey = MapTrackedUnits(em);
        foreach (var entry in ClientTable.Snapshot())
        {
            if (!byKey.TryGetValue(entry.Key, out var unit)) continue; // not streamed in (yet)
            try
            {
                var model = ModelOf(em, unit);
                if (model == null) continue;
                Apply(entry, model);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("UnitLooks: apply to " + entry.Key + " threw: " + e.Message);
            }
        }
    }

    /// <summary>Client entities with a model whose network id is tracked, keyed by that id.</summary>
    private static Dictionary<UnitNetKey, Entity> MapTrackedUnits(EntityManager em)
    {
        var map = new Dictionary<UnitNetKey, Entity>();
        var entities = Query.Entities(em, false, false,
            ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<HybridModelUser>(ComponentType.AccessMode.ReadOnly));
        foreach (var entity in entities)
        {
            if (!entity.TryRead(em, out NetworkId net) || net.Type != NetworkIdType.Normal) continue;
            var key = new UnitNetKey(net.Normal_Index, net.Normal_Generation);
            if (ClientTable.Get(key) != null) map[key] = entity;
        }
        return map;
    }

    /// <summary>The unit's model transform, or null while it has none (streaming, out of view).</summary>
    private static UnityEngine.Transform? ModelOf(EntityManager em, Entity unit)
    {
        byte* user = AiRuntime.RO<HybridModelUser>(em, unit, Layout.HybridModelUserHybridEntityOffset + sizeof(Entity));
        if (user == null) return null;
        var hybrid = *(Entity*)(user + Layout.HybridModelUserHybridEntityOffset);
        if (hybrid == Entity.Null || !em.Exists(hybrid)) return null;
        try
        {
            var transform = em.GetComponentObject<UnityEngine.Transform>(hybrid);
            return Alive(transform) ? transform : null;
        }
        catch
        {
            // The model entity exists before its GameObject finishes streaming.
            return null;
        }
    }

    private static void Apply(UnitLookClientTable.Entry entry, UnityEngine.Transform model)
    {
        int id = model.GetInstanceID();
        if (entry.ModelId != 0 && entry.ModelId != id)
        {
            // A new model instance: the old one goes back to vanilla (it may be pooled and reused).
            RestoreModel(entry);
        }

        var scale = model.localScale;
        var step = UnitLookModelStep.Plan(entry, id, new UnitVec3(scale.x, scale.y, scale.z), Book);
        if (step.WriteScale)
            model.localScale = new UnityEngine.Vector3(step.TargetScale.X, step.TargetScale.Y, step.TargetScale.Z);
        WriteColors(model, entry.Wanted, step.Colors);
        UnitLookModelStep.Commit(entry, id);
        entry.Model = model;
    }

    /// <summary>Put the entry's current model back to vanilla, if it is still alive. True when it was.</summary>
    private static bool RestoreModel(UnitLookClientTable.Entry entry)
    {
        var model = entry.Model as UnityEngine.Transform;
        int id = entry.ModelId;
        entry.Model = null;
        entry.ModelId = 0;
        var applied = entry.Applied;
        entry.Applied = null;
        if (id == 0) return false;
        try
        {
            if (!Alive(model)) return false;
            var (hasNatural, natural, colors) = UnitLookModelStep.Restore(id, applied, Book);
            if (hasNatural) model!.localScale = new UnityEngine.Vector3(natural.X, natural.Y, natural.Z);
            WriteColors(model!, null, colors);
            return true;
        }
        finally
        {
            Book.Forget(id);
        }
    }

    /// <summary>
    /// Write the color half of a transition to every renderer under <paramref name="model"/> whose
    /// material has the property. Written every pass while the look uses it, which is what re-asserts
    /// an overlay after the game's hit flash; a property the look does not use is only touched to
    /// restore it. Renderers without the properties (effect materials) are skipped.
    /// </summary>
    private static void WriteColors(UnityEngine.Transform model, UnitLook? look, UnitLookTransition colors)
    {
        if (!colors.WriteBase && !colors.RestoreBase && !colors.WriteOverlay && !colors.RestoreOverlay) return;
        EnsurePropertyIds();
        var block = _block ??= new UnityEngine.MaterialPropertyBlock();
        var renderers = model.GetComponentsInChildren<UnityEngine.Renderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (!Alive(renderer)) continue;
            var material = renderer.sharedMaterial;
            if (!Alive(material)) continue;
            bool hasBase = material.HasProperty(_baseId);
            bool hasOverlay = material.HasProperty(_overlayId);
            bool baseWrite = hasBase && (colors.WriteBase || colors.RestoreBase);
            bool overlayWrite = hasOverlay && (colors.WriteOverlay || colors.RestoreOverlay);
            if (!baseWrite && !overlayWrite) continue;

            // Read the renderer's block first so properties the game set are kept.
            renderer.GetPropertyBlock(block);
            if (baseWrite)
            {
                var own = material.GetColor(_baseId);
                var ownRgba = new LookRgba(own.r, own.g, own.b, own.a);
                var value = colors.WriteBase && look != null ? UnitLookMath.ComposeBase(ownRgba, look) : ownRgba;
                block.SetColor(_baseId, new UnityEngine.Color(value.R, value.G, value.B, value.A));
            }
            if (overlayWrite)
            {
                LookRgba value;
                if (colors.WriteOverlay && look != null) value = UnitLookMath.ComposeOverlay(look);
                else
                {
                    var own = material.GetColor(_overlayId);
                    value = new LookRgba(own.r, own.g, own.b, 0f);
                }
                block.SetColor(_overlayId, new UnityEngine.Color(value.R, value.G, value.B, value.A));
            }
            renderer.SetPropertyBlock(block);
        }
    }

    private static void EnsurePropertyIds()
    {
        if (_propertyIds) return;
        _baseId = UnityEngine.Shader.PropertyToID(BaseColorProperty);
        _overlayId = UnityEngine.Shader.PropertyToID(OverlayProperty);
        _propertyIds = true;
    }

    private static bool Alive(UnityEngine.Object? obj)
    {
        if (obj is null) return false;
        try { return !obj.WasCollected && obj != null; }
        catch { return false; }
    }
}
#endif
