#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.Network;
using Progenitor.Dev;
using Progenitor.Ecs;
using Progenitor.Players;
using Progenitor.Plugins;
using Progenitor.Systems;
using Progenitor.Units;
using Progenitor.WorldState;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Triggers;

/// <summary>
/// <see cref="PluginContext.Triggers"/>. Server watches of connected players. A client call
/// returns an inactive <see cref="AreaTrigger"/> so shared <c>OnLoad</c> code can register
/// on both sides. Each watch is <see cref="PluginContext.Track"/>ed, so unload stops it.
/// </summary>
public sealed class PluginTriggers
{
    private readonly PluginContext _ctx;
    private readonly AreaTriggerBook _book = new();
    private readonly Dictionary<int, Live> _live = new();
    private readonly List<AreaTriggerBook.Sighting> _sightings = new();
    private readonly List<AreaTriggerBook.Edge> _edges = new();
    private IDisposable? _tick;
    private bool _clientNoted;
    private bool _scanNoted;
    private int _sampling;

    internal PluginTriggers(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Watch a circle or an axis-aligned rectangle. Both have a finite height. The first time each
    /// connected player's position is seen, the watch only records whether they are already inside.
    /// Later crossings run <see cref="AreaTriggerOptions.OnEnter"/> and
    /// <see cref="AreaTriggerOptions.OnLeave"/>. Actions run on the server simulation thread and
    /// may call <c>ctx.Units.Spawn</c>.
    /// </summary>
    public AreaTrigger Watch(AreaTriggerOptions options)
    {
        _ctx.EnsureAttached();
        if (!MainThread.IsMainThread)
            throw new InvalidOperationException(
                "ctx.Triggers.Watch must run on the game main thread (OnLoad, a command, an event, or MainThread.Post).");
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Refusal() is { } refusal)
            throw new ArgumentException(refusal, nameof(options));

        if (!_ctx.IsServer)
        {
            if (!_clientNoted)
            {
                _clientNoted = true;
                _ctx.Log.LogInfo("Triggers: client registrations do nothing. The server watches players.");
            }

            var inactive = AreaTrigger.Inactive();
            _ctx.Track(inactive);
            return inactive;
        }

        ReadVolume(options, out AreaTriggerBook.Watch watch, out UnitVec3 center, out float radius, out UnitVec3 min, out UnitVec3 max);
        int id = _book.Add(watch);
        var live = new Live(options.OnEnter, options.OnLeave, options.Shape, center, radius, min, max);
        var handle = new AreaTrigger(() => Drop(id));
        _live[id] = live;
        _ctx.Track(handle);
        EnsureTick();
        _ctx.Log.LogInfo("Triggers: watching " + DescribeVolume(options) + " (" + DescribeEdges(options) + ").");
        return handle;
    }

    /// <summary>
    /// Watch a circle for enter only. <paramref name="height"/> is the full vertical size, centered
    /// on <paramref name="center"/>.
    /// </summary>
    public AreaTrigger OnEnter(UnitVec3 center, float radius, float height, Action<AreaTriggerHit> onEnter)
    {
        if (onEnter == null) throw new ArgumentNullException(nameof(onEnter));
        return Watch(new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Circle,
            Center = center,
            Radius = radius,
            Height = height,
            OnEnter = onEnter,
        });
    }

    /// <summary>
    /// Watch an axis-aligned rectangle for enter only. <paramref name="min"/> and <paramref name="max"/>
    /// are opposite corners; the order does not matter.
    /// </summary>
    public AreaTrigger OnEnter(UnitVec3 min, UnitVec3 max, Action<AreaTriggerHit> onEnter)
    {
        if (onEnter == null) throw new ArgumentNullException(nameof(onEnter));
        return Watch(new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Rectangle,
            Min = min,
            Max = max,
            OnEnter = onEnter,
        });
    }

    /// <summary>Watch a circle for leave only. <paramref name="height"/> is the full vertical size, centered on <paramref name="center"/>.</summary>
    public AreaTrigger OnLeave(UnitVec3 center, float radius, float height, Action<AreaTriggerHit> onLeave)
    {
        if (onLeave == null) throw new ArgumentNullException(nameof(onLeave));
        return Watch(new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Circle,
            Center = center,
            Radius = radius,
            Height = height,
            OnLeave = onLeave,
        });
    }

    /// <summary>Watch an axis-aligned rectangle for leave only. The corner order does not matter.</summary>
    public AreaTrigger OnLeave(UnitVec3 min, UnitVec3 max, Action<AreaTriggerHit> onLeave)
    {
        if (onLeave == null) throw new ArgumentNullException(nameof(onLeave));
        return Watch(new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Rectangle,
            Min = min,
            Max = max,
            OnLeave = onLeave,
        });
    }

    private static void ReadVolume(
        AreaTriggerOptions options,
        out AreaTriggerBook.Watch watch,
        out UnitVec3 center,
        out float radius,
        out UnitVec3 min,
        out UnitVec3 max)
    {
        bool reportEnter = options.OnEnter != null;
        bool reportLeave = options.OnLeave != null;
        if (options.Shape == AreaTriggerShape.Rectangle)
        {
            options.ReadRectangle(out min, out max);
            center = new UnitVec3(
                (min.X + max.X) * 0.5f,
                (min.Y + max.Y) * 0.5f,
                (min.Z + max.Z) * 0.5f);
            radius = 0f;
            watch = new AreaTriggerBook.Watch
            {
                Shape = AreaTriggerShape.Rectangle,
                X = center.X,
                Y = center.Y,
                Z = center.Z,
                MinX = min.X,
                MinY = min.Y,
                MinZ = min.Z,
                MaxX = max.X,
                MaxY = max.Y,
                MaxZ = max.Z,
                ReportEnter = reportEnter,
                ReportLeave = reportLeave,
            };
            return;
        }

        float half = options.Height * 0.5f;
        center = options.Center;
        radius = options.Radius;
        min = new UnitVec3(center.X - radius, center.Y - half, center.Z - radius);
        max = new UnitVec3(center.X + radius, center.Y + half, center.Z + radius);
        watch = new AreaTriggerBook.Watch
        {
            Shape = AreaTriggerShape.Circle,
            X = center.X,
            Y = center.Y,
            Z = center.Z,
            Radius = radius,
            HalfHeight = half,
            ReportEnter = reportEnter,
            ReportLeave = reportLeave,
        };
    }

    private void EnsureTick()
    {
        if (_tick != null) return;
        // Owned by this facade, not the plugin, so a plugin virtual system of the same name
        // cannot replace the tick. Unload still drops it: the handle is tracked.
        var anchor = VirtualSystemAnchor.AfterSimulation.WithSide(_ctx.Side);
        _tick = VirtualSystems.Add("progenitor.triggers", anchor, Tick, this);
        _ctx.Track(_tick);
    }

    private void Tick(VirtualSystemContext system)
    {
        if (_live.Count == 0)
        {
            StopTick();
            return;
        }

        if (!system.World.IsCreated) return;
        if (!AreaTriggerScanner.TryCollect(system.EntityManager, _sightings, out string error))
        {
            if (!_scanNoted)
            {
                _scanNoted = true;
                _ctx.Log.LogWarning("Triggers: player scan failed; this tick changes nothing. " + error);
            }

            return;
        }

        _scanNoted = false;
        _book.Step(WorldEpoch.Current, _sightings, _edges);
        _sampling++;
        try
        {
            for (int i = 0; i < _edges.Count; i++)
            {
                AreaTriggerBook.Edge edge = _edges[i];
                if (!_live.TryGetValue(edge.Id, out Live? live)) continue;
                Action<AreaTriggerHit>? action = edge.Enter ? live.OnEnter : live.OnLeave;
                if (action == null) continue;
                var hit = new AreaTriggerHit(
                    edge.AccountId,
                    edge.Name,
                    new UnitVec3(edge.X, edge.Y, edge.Z),
                    live.Shape,
                    live.Center,
                    live.Radius,
                    live.Min,
                    live.Max,
                    edge.Enter ? AreaTriggerEdge.Enter : AreaTriggerEdge.Leave);
                try
                {
                    action(hit);
                }
                catch (Exception e)
                {
                    _ctx.Log.LogError("Triggers: " + (edge.Enter ? "enter" : "leave") + " callback threw: " + e);
                }
            }
        }
        finally
        {
            _sampling--;
            if (_sampling == 0 && _live.Count == 0)
                StopTick();
        }
    }

    private void Drop(int id)
    {
        _live.Remove(id);
        _book.Remove(id);
        if (_live.Count == 0 && _sampling == 0)
            StopTick();
    }

    private void StopTick()
    {
        IDisposable? tick = _tick;
        _tick = null;
        tick?.Dispose();
    }

    private static string DescribeEdges(AreaTriggerOptions options)
    {
        if (options.OnEnter != null && options.OnLeave != null) return "enter, leave";
        return options.OnEnter != null ? "enter" : "leave";
    }

    private static string DescribeVolume(AreaTriggerOptions options)
    {
        if (options.Shape == AreaTriggerShape.Rectangle)
        {
            options.ReadRectangle(out UnitVec3 min, out UnitVec3 max);
            return "rectangle " + min + " to " + max;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return "circle radius " + options.Radius.ToString("0.##", culture)
            + " height " + options.Height.ToString("0.##", culture)
            + " at " + options.Center;
    }

    private sealed class Live
    {
        public readonly Action<AreaTriggerHit>? OnEnter;
        public readonly Action<AreaTriggerHit>? OnLeave;
        public readonly AreaTriggerShape Shape;
        public readonly UnitVec3 Center;
        public readonly float Radius;
        public readonly UnitVec3 Min;
        public readonly UnitVec3 Max;

        public Live(
            Action<AreaTriggerHit>? onEnter,
            Action<AreaTriggerHit>? onLeave,
            AreaTriggerShape shape,
            UnitVec3 center,
            float radius,
            UnitVec3 min,
            UnitVec3 max)
        {
            OnEnter = onEnter;
            OnLeave = onLeave;
            Shape = shape;
            Center = center;
            Radius = radius;
            Min = min;
            Max = max;
        }
    }
}

/// <summary>
/// One sample of connected players. A failed query returns false so the book does not treat
/// "the scan threw" as "everybody left" and fire a leave for each of them.
/// </summary>
internal static class AreaTriggerScanner
{
    internal static bool TryCollect(EntityManager em, List<AreaTriggerBook.Sighting> destination, out string error)
    {
        destination.Clear();
        error = "";
        EntityQuery query = default;
        bool created = false;
        try
        {
            var builder = new EntityQueryBuilder(Allocator.Temp);
            try
            {
                builder = builder.AddAll(ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
                builder = builder.WithOptions(EntityQueryOptions.IncludeDisabledEntities);
                query = em.CreateEntityQuery(ref builder);
            }
            finally
            {
                builder.Dispose();
            }

            created = true;
            var found = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < found.Length; i++)
                    ReadUser(em, found[i], destination);
            }
            finally
            {
                found.Dispose();
            }

            return true;
        }
        catch (Exception e)
        {
            destination.Clear();
            error = e.Message;
            return false;
        }
        finally
        {
            if (created)
                query.Dispose();
        }
    }

    private static void ReadUser(EntityManager em, Entity entity, List<AreaTriggerBook.Sighting> destination)
    {
        User user;
        try { user = em.GetComponentData<User>(entity); }
        catch { return; }

        if (!user.IsConnected || !PlayerDirectoryLogic.IsValidAccount(user.PlatformId))
            return;

        string name;
        try { name = user.CharacterName.ToString() ?? ""; }
        catch { name = ""; }

        float x = 0f, y = 0f, z = 0f;
        bool hasPosition = false;
        try
        {
            Entity character = user.LocalCharacter._Entity;
            if (character != Entity.Null && character.TryRead(em, out Translation translation))
            {
                var value = translation.Value;
                if (float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z))
                {
                    x = value.x;
                    y = value.y;
                    z = value.z;
                    hasPosition = true;
                }
            }
        }
        catch
        {
            hasPosition = false;
        }

        destination.Add(new AreaTriggerBook.Sighting(user.PlatformId, name, x, y, z, hasPosition));
    }
}
#endif
