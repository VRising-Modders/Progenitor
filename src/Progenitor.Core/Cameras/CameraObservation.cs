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
    /// starts it for <see cref="CameraObservation.MaxRenewSeconds"/>. A previous lease of this plugin for the same user ends
    /// (<see cref="CameraObservationEndReason.Replaced"/>). The user has one streaming origin: a lease another holder (another
    /// plugin, or Core's container preview) runs at the same target is shared, one at another point refuses this call.
    /// See <c>docs/modules.md</c>, "Camera preview".
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
        var lease = new CameraObservation(this, options);
        SharedObservation.Acquire(world, user, point.Value, lease);
        _observations.Add(user, lease);
        return lease;
    }

    /// <summary>
    /// A server streaming-origin lease from <see cref="Observe(Entity, Entity, CameraObserveOptions)"/>. It always has a
    /// deadline: a timed start is at most 10 seconds and <see cref="Renew"/> / <see cref="Hold"/> grant at most
    /// <see cref="MaxRenewSeconds"/> from the call, so a lease nobody renews ends. It also ends on Dispose, when the
    /// user disconnects, when the user's real character moves more than <see cref="LeaveDistanceMeters"/>, when
    /// something else writes the streaming origin, when a newer Observe replaces it, and on unload. When another holder
    /// shares the user's lease at the same point, this handle's end only drops this holder.
    /// </summary>
    public sealed class CameraObservation : IDisposable, IObservationHolder
    {
        /// <summary>The most one <see cref="Renew"/> or <see cref="Hold"/> grants, in seconds (ten minutes).</summary>
        public const float MaxRenewSeconds = CameraObservationPolicy.MaxRenewSeconds;

        /// <summary>How far the user's real character may move, in meters, before the lease ends.</summary>
        public const float LeaveDistanceMeters = CameraObservationPolicy.LeaveDistanceMeters;

        private readonly PluginCamera _camera;
        private readonly CameraObservationPolicy _policy;
        private Action<CameraObservationEndReason>? _ended;
        private SharedObservation? _lease;

        /// <summary>True until the lease ends.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>True while the lease runs on a <see cref="Hold"/> or a renewal longer than 10 seconds.</summary>
        public bool IsHeld => Active && _policy.IsHeld;

        /// <summary>Why it ended; <see cref="CameraObservationEndReason.None"/> while <see cref="Active"/>.</summary>
        public CameraObservationEndReason EndReason { get; private set; }

        /// <summary>Seconds until the lease expires unless renewed; 0 once it ended.</summary>
        public double SecondsLeft => Active ? Math.Max(0, _policy.Deadline - SharedObservation.Now) : 0;

        internal CameraObservation(PluginCamera camera, CameraObserveOptions options)
        {
            _camera = camera; _ended = options.Ended;
            _policy = new CameraObservationPolicy(options.Hold ? CameraObservationPolicy.MaxTimedSeconds : options.Seconds, options.Hold, SharedObservation.Now);
        }

        CameraObservationPolicy IObservationHolder.Policy => _policy;
        string IObservationHolder.Name => "camera:" + _camera._ctx.Name;
        void IObservationHolder.Attached(SharedObservation lease) => _lease = lease;
        void IObservationHolder.LeaseEnded(CameraObservationEndReason reason, bool notify) => Finish(reason, notify);

        /// <summary>
        /// Keep the lease for <paramref name="seconds"/> from now (more than 0, at most <see cref="MaxRenewSeconds"/>),
        /// replacing the old deadline. Returns false when the lease already ended.
        /// </summary>
        public bool Renew(float seconds)
        {
            if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaxRenewSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (!Active) return false;
            _policy.Renew(SharedObservation.Now, seconds);
            _lease?.Rearm();
            return true;
        }

        /// <summary>Renew for the full <see cref="MaxRenewSeconds"/>. Returns false when the lease already ended.</summary>
        public bool Hold()
        {
            if (!Active) return false;
            _policy.Hold(SharedObservation.Now);
            _lease?.Rearm();
            return true;
        }

        /// <summary>End the lease and restore ordinary visibility (<see cref="CameraObservationEndReason.Disposed"/>).</summary>
        public void Dispose() => End(CameraObservationEndReason.Disposed, notify: true);

        internal void End(CameraObservationEndReason reason, bool notify)
        {
            if (!Active) return;
            var lease = _lease;
            if (lease != null) lease.Leave(this, reason, notify);
            else Finish(reason, notify);
        }

        private void Finish(CameraObservationEndReason reason, bool notify)
        {
            if (!Active) return;
            Active = false;
            EndReason = reason;
            _lease = null;
            foreach (var pair in _camera._observations)
            {
                if (!ReferenceEquals(pair.Value, this)) continue;
                _camera._observations.Remove(pair.Key);
                break;
            }
            Plugin.Logger?.LogInfo("Camera: observation ended (" + reason + ").");
            var ended = _ended; _ended = null;
            if (notify && ended != null)
            {
                try { ended(reason); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Camera: observation ended callback threw: " + e.Message); }
            }
        }
    }
}

/// <summary>One holder of a <see cref="SharedObservation"/>: a plugin's camera lease or Core's container preview.</summary>
internal interface IObservationHolder
{
    /// <summary>This holder's own clock: its deadline, Renew and Hold.</summary>
    CameraObservationPolicy Policy { get; }

    /// <summary>For log lines and the Dev bridge.</summary>
    string Name { get; }

    /// <summary>Called once when the holder joins a lease.</summary>
    void Attached(SharedObservation lease);

    /// <summary>Called once when the holder leaves the lease, for any reason. Must not call back into the lease.</summary>
    void LeaseEnded(CameraObservationEndReason reason, bool notify);
}

/// <summary>
/// The one server streaming-origin lease a user can have (<c>User.ProxySyncPosition</c>), shared by every holder at the
/// same point (<see cref="ObservationShareRules"/>). Core-owned: its hook and timer are not tied to any plugin, so one
/// holder's plugin unloading leaves the others streaming. Each frame before <c>UserActivityGridSystem</c> it runs the
/// lease-wide safety checks (ending every holder), expires holders whose own deadline passed, and writes the origin
/// while any holder is left. The last holder leaving restores the user's original origin and destroys the anchor.
/// </summary>
internal sealed unsafe class SharedObservation
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<Entity, SharedObservation> ByUser = new();

    internal static double Now => Clock.Elapsed.TotalSeconds;

    private readonly World _world;
    private readonly Entity _user;
    private readonly Entity _anchor;
    private readonly float3 _point;
    private readonly ModifiableFloat3 _originalProxy;
    private readonly Entity _character;
    private readonly float3 _standing;
    private readonly bool _standingKnown;
    private readonly ObservationShareBook<IObservationHolder> _holders = new();
    private IDisposable? _hook;
    private int _timerGeneration;
    private bool _proxySuspended;

    /// <summary>True until the last holder leaves.</summary>
    internal bool Active { get; private set; } = true;

    internal Entity User => _user;

    /// <summary>The user's platform id (the persisted record's key).</summary>
    internal ulong Platform { get; }

    /// <summary>True while a world save is being written: the user's original origin is in place and the point is not written.</summary>
    internal bool ProxySuspended => _proxySuspended;

    internal float3 Point => _point;

    internal int HolderCount => _holders.Count;

    /// <summary>The running lease for <paramref name="user"/>, or null.</summary>
    internal static SharedObservation? For(Entity user) => ByUser.TryGetValue(user, out var lease) && lease.Active ? lease : null;

    /// <summary>Every running lease (Dev bridge status).</summary>
    internal static List<SharedObservation> All() => new(ByUser.Values);

    /// <summary>
    /// Add <paramref name="holder"/> to the user's lease at <paramref name="point"/>: start one, or join the running one at
    /// the same point. Throws <see cref="InvalidOperationException"/> when the user's origin is taken (a lease at another
    /// point, or something outside Progenitor wrote it).
    /// </summary>
    internal static SharedObservation Acquire(World world, Entity user, float3 point, IObservationHolder holder)
    {
        if (holder == null) throw new ArgumentNullException(nameof(holder));
        var em = world.EntityManager;
        var running = For(user);
        bool same = running != null && ObservationShareRules.SamePoint(running._point.x, running._point.y, running._point.z, point.x, point.y, point.z);
        bool elsewhere = running == null && math.any(em.GetComponentData<User>(user).ProxySyncPosition.Value != float3.zero);
        // An origin left at a recorded lease point by an earlier run (a crash; the game saves it) is ours: put it back and go on.
        if (elsewhere && ObservationGuard.TryRepair(world, user)) elsewhere = false;
        switch (ObservationShareRules.Decide(running != null, same, elsewhere))
        {
            case ObservationJoin.Join:
                running!._holders.Add(holder, holder.Policy);
                holder.Attached(running);
                running.Rearm();
                Plugin.Logger?.LogInfo("Camera: observation shared by " + holder.Name + " (" + running._holders.Count + " holders).");
                return running;
            case ObservationJoin.Refuse:
                throw new InvalidOperationException("Another view already owns this user's streaming origin.");
        }

        var lease = new SharedObservation(world, user, point);
        try
        {
            lease._holders.Add(holder, holder.Policy);
            holder.Attached(lease);
            ByUser[user] = lease;
            lease.Start();
        }
        catch
        {
            lease.Close(CameraObservationEndReason.Failed, notify: false);
            throw;
        }
        return lease;
    }

    /// <summary>End every lease (Core unload); holders get <see cref="CameraObservationEndReason.Unloaded"/> with no notification.</summary>
    internal static void EndAll()
    {
        foreach (var lease in new List<SharedObservation>(ByUser.Values)) lease.Close(CameraObservationEndReason.Unloaded, notify: false);
        ByUser.Clear();
    }

    private SharedObservation(World world, Entity user, float3 point)
    {
        _world = world; _user = user; _point = point;
        var em = world.EntityManager;
        var row = em.GetComponentData<User>(user);
        _originalProxy = row.ProxySyncPosition;
        Platform = row.PlatformId;
        try { _character = row.LocalCharacter._Entity; } catch { _character = Entity.Null; }
        if (_character != Entity.Null && _character.TryRead<Translation>(em, out var at)) { _standing = at.Value; _standingKnown = true; }
        _anchor = em.CreateEntity();
    }

    private void Start()
    {
        var em = _world.EntityManager;
        _anchor.Add<Translation>(em);
        _anchor.Add<PersistenceV2.DontSaveEntity>(em);
        _anchor.Write(em, new Translation { Value = _point });
        // A world save is being written: start with the original origin in place; the point goes in after the save.
        _proxySuspended = Persistence.WorldSaveGuard.Active;
        ObservationGuard.Put(Platform, _point, _originalProxy.Value);
        _hook = SystemHooks.Before<UserActivityGridSystem>(c =>
        {
            if (c.World != _world || !Active) return;
            Check(c.World.EntityManager, writeProxy: true);
        });
        Rearm();
    }

    /// <summary>Holder deadlines changed (a join, a Renew, a Hold): post one timer for the earliest one.</summary>
    internal void Rearm()
    {
        if (!Active) return;
        int generation = ++_timerGeneration;
        double next = _holders.NextDeadline;
        if (double.IsInfinity(next)) return;
        MainThread.PostDelayed(Math.Max(0.05, next - Now) + 0.1, () => OnTimer(generation));
    }

    /// <summary>One holder leaves (Dispose, Replaced, its plugin unloading). The last one ends the lease.</summary>
    internal void Leave(IObservationHolder holder, CameraObservationEndReason reason, bool notify)
    {
        if (!_holders.Remove(holder)) { holder.LeaseEnded(reason, notify); return; }
        holder.LeaseEnded(reason, notify);
        if (_holders.Count == 0) Close(reason, notify: false);
        else Rearm();
    }

    private void OnTimer(int generation)
    {
        if (!Active || generation != _timerGeneration) return;
        if (!_world.IsCreated) { Close(CameraObservationEndReason.Unloaded, notify: false); return; }
        if (Check(_world.EntityManager, writeProxy: false)) Rearm();
    }

    /// <summary>One check; true when the lease is still running.</summary>
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
            reason = ObservationShareRules.Safety(s);
            if (reason == CameraObservationEndReason.None)
            {
                foreach (var holder in _holders.TakeExpired(Now)) holder.LeaseEnded(CameraObservationEndReason.Expired, notify: true);
                if (_holders.Count == 0) reason = CameraObservationEndReason.Expired;
                else if (writeProxy && !_proxySuspended) WriteProxy(_point);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Camera: observation check threw: " + e.Message);
            reason = CameraObservationEndReason.Failed;
        }

        if (reason == CameraObservationEndReason.None) return true;
        Close(reason, notify: true);
        return false;
    }

    /// <summary>End the lease for every remaining holder with <paramref name="reason"/> and restore the origin.</summary>
    private void Close(CameraObservationEndReason reason, bool notify)
    {
        if (!Active) return;
        Active = false;
        if (ByUser.TryGetValue(_user, out var current) && ReferenceEquals(current, this)) ByUser.Remove(_user);
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
        ObservationGuard.Remove(Platform);
        Plugin.Logger?.LogInfo("Camera: streaming origin restored (" + reason + ").");
        foreach (var holder in _holders.TakeAll())
        {
            try { holder.LeaseEnded(reason, notify); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Camera: observation holder end threw: " + e.Message); }
        }
    }

    /// <summary>
    /// The shared world-save guard began its suspension: put the user's original origin back (the game saves
    /// <c>User.ProxySyncPosition</c>) and stop writing the point until <see cref="ResumeProxy"/>. While the origin is
    /// back, the chest and its surroundings stop streaming to the previewing client for the save's duration.
    /// </summary>
    internal void SuspendProxy()
    {
        if (!Active) return;
        _proxySuspended = true;
        if (!_world.IsCreated) return;
        var em = _world.EntityManager;
        if (_user.Exists(em) && math.all(em.GetComponentData<User>(_user).ProxySyncPosition.Value == _point)) WriteProxy(_originalProxy.Value);
    }

    /// <summary>The save is written: the frame hook writes the point again from the next frame.</summary>
    internal void ResumeProxy() => _proxySuspended = false;

    private void WriteProxy(float3 point) => WriteProxyRaw(_world, _user, point);

    /// <summary>The one write of a user's streaming origin (an overlay write at the layout offset).</summary>
    internal static void WriteProxyRaw(World world, Entity user, float3 point)
    {
        var data = AiRuntime.RW<User>(world.EntityManager, user, Layout.ObservationProxyPositionOffset + 12);
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
