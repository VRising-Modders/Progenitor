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
    private readonly Dictionary<Entity, CameraObservation> _observations = new();

    /// <summary>Server-only temporary streaming origin. The caller must authorize the target.
    /// The user's character is never moved. Dispose or the bounded timeout restores ordinary visibility.
    /// The returned object is a <see cref="CameraObservation"/>; cast it to <see cref="CameraObservation.Renew"/> or
    /// <see cref="CameraObservation.Hold"/>, or use the options overload.</summary>
    public IDisposable Observe(Entity user, Entity target, float seconds = 8)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > CameraObservationPolicy.MaxTimedSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        return Observe(user, target, new CameraObserveOptions { Seconds = seconds });
    }

    /// <summary>
    /// <see cref="Observe(Entity, Entity, float)"/> with options and a renewable lease. <see cref="CameraObserveOptions.Hold"/>
    /// starts it for <see cref="CameraObservation.MaxRenewSeconds"/>. A previous lease for the same user ends
    /// (<see cref="CameraObservationEndReason.Replaced"/>). See <c>docs/modules.md</c>, "Camera preview".
    /// </summary>
    public CameraObservation Observe(Entity user, Entity target, CameraObserveOptions options)
    {
        _ctx.EnsureAttached();
        if (_ctx.IsClient) throw new InvalidOperationException("Observe runs on the server.");
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!options.Hold && (!float.IsFinite(options.Seconds) || options.Seconds <= 0 || options.Seconds > CameraObservationPolicy.MaxTimedSeconds))
            throw new ArgumentOutOfRangeException(nameof(options), "Seconds must be more than 0 and at most 10; use Hold or Renew for longer.");
        var world = Worlds.Server ?? throw new InvalidOperationException("No server world.");
        var em = world.EntityManager;
        if (!user.Exists(em) || !user.Has<User>(em) || !target.TryRead<Translation>(em, out var point))
            throw new ArgumentException("User or observation target is unavailable.");
        if (_observations.Remove(user, out var previous)) previous.End(CameraObservationEndReason.Replaced, notify: true);
        var lease = new CameraObservation(this, world, user, point.Value, options);
        _observations.Add(user, lease);
        return lease;
    }

    /// <summary>
    /// A server streaming-origin lease from <see cref="Observe(Entity, Entity, CameraObserveOptions)"/>. It always has a
    /// deadline: a timed start is at most 10 seconds and <see cref="Renew"/> / <see cref="Hold"/> grant at most
    /// <see cref="MaxRenewSeconds"/> from the call, so a lease nobody renews ends. It also ends on Dispose, when the
    /// user disconnects, when the user's real character moves more than <see cref="LeaveDistanceMeters"/>, when
    /// something else writes the streaming origin, when a newer Observe replaces it, and on unload.
    /// </summary>
    public sealed unsafe class CameraObservation : IDisposable
    {
        /// <summary>The most one <see cref="Renew"/> or <see cref="Hold"/> grants, in seconds (ten minutes).</summary>
        public const float MaxRenewSeconds = CameraObservationPolicy.MaxRenewSeconds;

        /// <summary>How far the user's real character may move, in meters, before the lease ends.</summary>
        public const float LeaveDistanceMeters = CameraObservationPolicy.LeaveDistanceMeters;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly PluginCamera _camera;
        private readonly World _world;
        private readonly Entity _user;
        private readonly Entity _anchor;
        private readonly float3 _point;
        private readonly ModifiableFloat3 _originalProxy;
        private readonly CameraObservationPolicy _policy;
        private readonly Entity _character;
        private readonly float3 _standing;
        private readonly bool _standingKnown;
        private Action<CameraObservationEndReason>? _ended;
        private IDisposable? _hook;
        private int _timerGeneration;

        /// <summary>True until the lease ends.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>True while the lease runs on a <see cref="Hold"/> or a renewal longer than 10 seconds.</summary>
        public bool IsHeld => Active && _policy.IsHeld;

        /// <summary>Why it ended; <see cref="CameraObservationEndReason.None"/> while <see cref="Active"/>.</summary>
        public CameraObservationEndReason EndReason { get; private set; }

        /// <summary>Seconds until the lease expires unless renewed; 0 once it ended.</summary>
        public double SecondsLeft => Active ? Math.Max(0, _policy.Deadline - Now) : 0;

        private static double Now => Clock.Elapsed.TotalSeconds;

        internal CameraObservation(PluginCamera camera, World world, Entity user, float3 point, CameraObserveOptions options)
        {
            _camera = camera; _world = world; _user = user; _point = point; _ended = options.Ended;
            _policy = new CameraObservationPolicy(options.Hold ? CameraObservationPolicy.MaxTimedSeconds : options.Seconds, options.Hold, Now);
            var em = world.EntityManager;
            var row = em.GetComponentData<User>(user);
            _originalProxy = row.ProxySyncPosition;
            if (math.any(_originalProxy.Value != float3.zero)) throw new InvalidOperationException("Another view already owns this user's streaming origin.");
            try { _character = row.LocalCharacter._Entity; } catch { _character = Entity.Null; }
            if (_character != Entity.Null && _character.TryRead<Translation>(em, out var at)) { _standing = at.Value; _standingKnown = true; }
            _anchor = em.CreateEntity();
            try
            {
                _anchor.Add<Translation>(em);
                _anchor.Add<PersistenceV2.DontSaveEntity>(em);
                _anchor.Write(em, new Translation { Value = point });
                _hook = SystemHooks.Before<UserActivityGridSystem>(c =>
                {
                    if (c.World != _world || !Active) return;
                    Check(c.World.EntityManager, writeProxy: true);
                }, new HookOptions { PluginId = camera._ctx.Guid });
                PostTimer();
            }
            catch { End(CameraObservationEndReason.Failed, notify: false); throw; }
        }

        /// <summary>
        /// Keep the lease for <paramref name="seconds"/> from now (more than 0, at most <see cref="MaxRenewSeconds"/>),
        /// replacing the old deadline. Returns false when the lease already ended.
        /// </summary>
        public bool Renew(float seconds)
        {
            if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaxRenewSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (!Active) return false;
            _policy.Renew(Now, seconds);
            PostTimer();
            return true;
        }

        /// <summary>Renew for the full <see cref="MaxRenewSeconds"/>. Returns false when the lease already ended.</summary>
        public bool Hold()
        {
            if (!Active) return false;
            _policy.Hold(Now);
            PostTimer();
            return true;
        }

        /// <summary>End the lease and restore ordinary visibility (<see cref="CameraObservationEndReason.Disposed"/>).</summary>
        public void Dispose() => End(CameraObservationEndReason.Disposed, notify: true);

        /// <summary>One expiry timer per deadline; an older timer that fires after a Renew does nothing.</summary>
        private void PostTimer()
        {
            if (!Active) return;
            int generation = ++_timerGeneration;
            MainThread.PostDelayed(Math.Max(0.05, _policy.Deadline - Now) + 0.1, () => OnTimer(generation), _camera._ctx.Owner);
        }

        private void OnTimer(int generation)
        {
            if (!Active || generation != _timerGeneration) return;
            if (!_world.IsCreated) { End(CameraObservationEndReason.Unloaded, notify: true); return; }
            if (Check(_world.EntityManager, writeProxy: false)) PostTimer();
        }

        /// <summary>One end check; true when the lease is still running.</summary>
        private bool Check(EntityManager em, bool writeProxy)
        {
            CameraObservationEndReason reason;
            try
            {
                var s = new CameraObservationSample();
                if (_user.Exists(em))
                {
                    var row = em.GetComponentData<User>(_user);
                    s.UserConnected = row.IsConnected;
                    var proxy = row.ProxySyncPosition.Value;
                    s.OriginOurs = !math.any(proxy != float3.zero) || !math.any(proxy != _point);
                }
                if (!_standingKnown) { s.CharacterRead = true; s.MovedMeters = 0; }
                else if (_character.Exists(em) && _character.TryRead<Translation>(em, out var at))
                {
                    s.CharacterRead = true;
                    s.MovedMeters = math.distance(at.Value.xz, _standing.xz);
                }
                reason = _policy.Decide(Now, s);
                if (reason == CameraObservationEndReason.None && writeProxy) WriteProxy(_point);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Camera: observation check threw: " + e.Message);
                reason = CameraObservationEndReason.Failed;
            }

            if (reason == CameraObservationEndReason.None) return true;
            End(reason, notify: true);
            return false;
        }

        internal void End(CameraObservationEndReason reason, bool notify)
        {
            if (!Active) return;
            Active = false;
            EndReason = reason;
            if (_camera._observations.TryGetValue(_user, out var current) && ReferenceEquals(current, this)) _camera._observations.Remove(_user);
            _hook?.Dispose(); _hook = null;
            try
            {
                if (_world.IsCreated)
                {
                    var em = _world.EntityManager;
                    try
                    {
                        if (_user.Exists(em) && math.all(em.GetComponentData<User>(_user).ProxySyncPosition.Value == _point)) WriteProxy(_originalProxy.Value);
                    }
                    finally { if (_anchor != Entity.Null && _anchor.Exists(em)) em.DestroyEntity(_anchor); }
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Camera: observation restore threw: " + e.Message); }
            Plugin.Logger?.LogInfo("Camera: observation ended (" + reason + ").");
            var ended = _ended; _ended = null;
            if (notify && ended != null)
            {
                try { ended(reason); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Camera: observation ended callback threw: " + e.Message); }
            }
        }

        private void WriteProxy(float3 point)
        {
            var data = AiRuntime.RW<User>(_world.EntityManager, _user, Layout.ObservationProxyPositionOffset + 12);
            if (data == null) throw new InvalidOperationException("User observation layout unavailable.");
            *(float3*)(data + Layout.ObservationProxyPositionOffset) = point;
        }
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
