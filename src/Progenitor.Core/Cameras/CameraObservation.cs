#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Dev;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Progenitor.Units;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Cameras;

public sealed partial class PluginCamera
{
    private readonly Dictionary<Entity, ServerObservation> _observations = new();

    /// <summary>Server-only temporary streaming origin. The caller must authorize the target.
    /// The user's character is never moved. Dispose or the bounded timeout restores ordinary visibility.</summary>
    public IDisposable Observe(Entity user, Entity target, float seconds = 8)
    {
        _ctx.EnsureAttached();
        if (_ctx.IsClient) throw new InvalidOperationException("Observe runs on the server.");
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > 10) throw new ArgumentOutOfRangeException(nameof(seconds));
        var world = Worlds.Server ?? throw new InvalidOperationException("No server world.");
        var em = world.EntityManager;
        if (!user.Exists(em) || !user.Has<User>(em) || !target.TryRead<Translation>(em, out var point))
            throw new ArgumentException("User or observation target is unavailable.");
        if (_observations.Remove(user, out var previous)) previous.Dispose();
        var lease = new ServerObservation(_ctx, world, user, point.Value);
        _observations.Add(user, lease);
        MainThread.PostDelayed(seconds + 0.1, () =>
        {
            lease.Dispose();
            if (_observations.TryGetValue(user, out var active) && ReferenceEquals(active, lease)) _observations.Remove(user);
        }, _ctx.Owner);
        return lease;
    }
}

internal sealed unsafe class ServerObservation : IDisposable
{
    private readonly World _world;
    private readonly Entity _user;
    private readonly Entity _anchor;
    private readonly float3 _point;
    private readonly ModifiableFloat3 _originalProxy;
    private IDisposable? _hook;
    private bool _disposed;
    internal ServerObservation(PluginContext ctx, World world, Entity user, float3 point)
    {
        _world = world; _user = user; _point = point;
        _originalProxy = world.EntityManager.GetComponentData<User>(user).ProxySyncPosition;
        if (math.any(_originalProxy.Value != float3.zero)) throw new InvalidOperationException("Another view already owns this user's streaming origin.");
        _anchor = world.EntityManager.CreateEntity();
        try
        {
            _anchor.Add<Translation>(world.EntityManager);
            _anchor.Add<PersistenceV2.DontSaveEntity>(world.EntityManager);
            _anchor.Write(world.EntityManager, new Translation { Value = point });
            _hook = SystemHooks.Before<UserActivityGridSystem>(c =>
            {
                if (c.World != _world || _disposed || !_user.Exists(c.World.EntityManager)) return;
                var row = c.World.EntityManager.GetComponentData<User>(_user);
                if (!row.IsConnected) { Dispose(); return; }
                try
                {
                    if (math.any(row.ProxySyncPosition.Value != float3.zero) && math.any(row.ProxySyncPosition.Value != _point)) { Dispose(); return; }
                    WriteProxy(_point);
                }
                catch { Dispose(); throw; }
            }, new HookOptions { PluginId = ctx.Guid });
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook?.Dispose(); _hook = null;
        if (!_world.IsCreated) return;
        var em = _world.EntityManager;
        try
        {
            if (_user.Exists(em) && math.all(em.GetComponentData<User>(_user).ProxySyncPosition.Value == _point)) WriteProxy(_originalProxy.Value);
        }
        finally { if (_anchor.Exists(em)) em.DestroyEntity(_anchor); }
    }

    private void WriteProxy(float3 point)
    {
        var data = AiRuntime.RW<User>(_world.EntityManager, _user, Layout.ObservationProxyPositionOffset + 12);
        if (data == null) throw new InvalidOperationException("User observation layout unavailable.");
        *(float3*)(data + Layout.ObservationProxyPositionOffset) = point;
    }
}

/// <summary>Invisible, immobile client observation entity. Supplies only the render origin;
/// the player's user, character, controller and inventories keep their normal identity.</summary>
internal sealed unsafe class ClientObservation : IDisposable
{
    private readonly World _world;
    private readonly Entity _anchor;
    private readonly Entity _common;
    private readonly float3 _position;
    private readonly CommonClientDataSystem _system;
    private IDisposable? _hook;
    private Entity _previousEntity;
    private float3 _previousPosition;
    private bool _disposed;

    internal ClientObservation(PluginContext ctx, UnitVec3 point, int compressedHeight)
    {
        _world = Worlds.Client ?? throw new InvalidOperationException("No client world.");
        var em = _world.EntityManager;
        _system = _world.GetExistingSystemManaged<CommonClientDataSystem>() ?? throw new InvalidOperationException("No client view data.");
        foreach (var entity in Query.Entities(em, includeDisabled: false, includeSystems: true, ComponentIndex.Of<CommonClientData>(ComponentType.AccessMode.ReadWrite))) { _common = entity; break; }
        if (_common == Entity.Null) throw new InvalidOperationException("No client view singleton.");
        _position = new float3(point.X, point.Y, point.Z);
        _anchor = em.CreateEntity();
        try
        {
            _anchor.Add<Translation>(em);
            _anchor.Write(em, new Translation { Value = _position });
            if (compressedHeight >= 0)
            {
                _anchor.Add<TilePosition>(em);
                _anchor.Write(em, new TilePosition { CompressedHeight = checked((ushort)compressedHeight) });
            }
            Apply();
            _hook = SystemHooks.After<CommonClientDataSystem>(c => { if (c.World == _world && !_disposed) Apply(); }, new HookOptions { PluginId = ctx.Guid });
        }
        catch { Dispose(); throw; }
    }

    private void Apply()
    {
        var data = _system._SingletonData;
        var local = data.LocalUser;
        if (local.ControlledEntity != _anchor) { _previousEntity = local.ControlledEntity; _previousPosition = local.ControlledPosition; }
        Write(_anchor, _position);
    }

    private void Write(Entity entity, float3 position)
    {
        var em = _world.EntityManager;
        byte* data = AiRuntime.RW<CommonClientData>(em, _common, Layout.ObservationControlledPositionOffset + 12);
        if (data == null) throw new InvalidOperationException("Client observation layout unavailable.");
        AiRuntime.WriteEntity(data + Layout.ObservationLocalUserOffset + Layout.ObservationControlledEntityOffset, entity);
        *(float3*)(data + Layout.ObservationLocalUserOffset + Layout.ObservationControlledPositionOffset) = position;
        var cached = _system._SingletonData;
        var local = cached.LocalUser;
        local.ControlledEntity = entity; local.ControlledPosition = position;
        cached.LocalUser = local;
        _system._SingletonData = cached;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _hook?.Dispose(); _hook = null;
        if (!_world.IsCreated) return;
        var em = _world.EntityManager;
        if (_system._SingletonData.LocalUser.ControlledEntity == _anchor)
        {
            if (_previousEntity.TryRead<Translation>(em, out var current)) _previousPosition = current.Value;
            Write(_previousEntity, _previousPosition);
        }
        if (_anchor.Exists(em)) em.DestroyEntity(_anchor);
    }
}
#endif
