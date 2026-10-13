using System;
using System.Collections.Generic;
using Progenitor.Units;

namespace Progenitor.Triggers;

/// <summary>Which way a player crossed an <see cref="AreaTrigger"/>.</summary>
public enum AreaTriggerEdge
{
    /// <summary>The player was outside and is now inside.</summary>
    Enter = 0,

    /// <summary>The player was inside and is now outside, or left the server while inside.</summary>
    Leave = 1,
}

/// <summary>The two area shapes. Both have a finite height, so a floor above or below is outside.</summary>
public enum AreaTriggerShape
{
    /// <summary>A vertical cylinder: <see cref="AreaTriggerOptions.Radius"/> around <see cref="AreaTriggerOptions.Center"/> on x and z, and <see cref="AreaTriggerOptions.Height"/> centered on its height.</summary>
    Circle = 0,

    /// <summary>An axis-aligned box from <see cref="AreaTriggerOptions.Min"/> to <see cref="AreaTriggerOptions.Max"/>.</summary>
    Rectangle = 1,
}

/// <summary>
/// One crossing, passed to <see cref="AreaTriggerOptions.OnEnter"/> or
/// <see cref="AreaTriggerOptions.OnLeave"/>. <see cref="Position"/> is the player.
/// <see cref="Center"/>, <see cref="Radius"/>, <see cref="Min"/> and <see cref="Max"/> are the area
/// as it was registered. For a circle, <see cref="Min"/> and <see cref="Max"/> are the cylinder's
/// axis-aligned bounds, not its round footprint. For a rectangle, <see cref="Radius"/> is zero.
/// </summary>
public readonly struct AreaTriggerHit
{
    /// <summary><c>User.PlatformId</c> of the player who crossed.</summary>
    public ulong PlatformId { get; }

    /// <summary>Display name at this sample, or empty when the game had none.</summary>
    public string CharacterName { get; }

    /// <summary>Where the player was when they crossed. A disconnect uses the last inside position.</summary>
    public UnitVec3 Position { get; }

    /// <summary>Circle or rectangle.</summary>
    public AreaTriggerShape Shape { get; }

    /// <summary>The middle of the volume.</summary>
    public UnitVec3 Center { get; }

    /// <summary>Horizontal radius in meters for a circle. Zero for a rectangle.</summary>
    public float Radius { get; }

    /// <summary>Low corner of the volume's bounds, inclusive.</summary>
    public UnitVec3 Min { get; }

    /// <summary>High corner of the volume's bounds, inclusive.</summary>
    public UnitVec3 Max { get; }

    /// <summary>Enter or leave.</summary>
    public AreaTriggerEdge Edge { get; }

    /// <summary>True when <see cref="Edge"/> is <see cref="AreaTriggerEdge.Enter"/>.</summary>
    public bool Entered => Edge == AreaTriggerEdge.Enter;

    internal AreaTriggerHit(
        ulong platformId,
        string? characterName,
        UnitVec3 position,
        AreaTriggerShape shape,
        UnitVec3 center,
        float radius,
        UnitVec3 min,
        UnitVec3 max,
        AreaTriggerEdge edge)
    {
        PlatformId = platformId;
        CharacterName = characterName ?? "";
        Position = position;
        Shape = shape;
        Center = center;
        Radius = radius;
        Min = min;
        Max = max;
        Edge = edge;
    }
}

/// <summary>
/// How to watch one area. <see cref="PluginTriggers.Watch"/> reads this once; changing it
/// afterwards does not move the area or retarget the actions.
/// </summary>
public sealed class AreaTriggerOptions
{
    /// <summary>
    /// Extra meters past every face a player must walk before a leave fires, so standing on the
    /// rim does not enter and leave every tick. Enter uses the registered volume. The skin grows
    /// the circle's radius and its height, and grows a rectangle on every side, including up and down.
    /// </summary>
    public const float LeaveSkin = 0.5f;

    /// <summary><see cref="AreaTriggerShape.Circle"/> unless set to <see cref="AreaTriggerShape.Rectangle"/>.</summary>
    public AreaTriggerShape Shape { get; set; }

    /// <summary>Center of a circle. The vertical slab is <see cref="Height"/> meters tall, centered here. Unused for a rectangle.</summary>
    public UnitVec3 Center { get; set; }

    /// <summary>Horizontal radius in meters (x and z) for a circle. Zero is the vertical line at <see cref="Center"/>. Unused for a rectangle.</summary>
    public float Radius { get; set; }

    /// <summary>Full height of a circle in meters, centered on <see cref="Center"/>. Required, and greater than zero.</summary>
    public float Height { get; set; }

    /// <summary>One corner of a rectangle. The opposite corner is <see cref="Max"/>. Order does not matter.</summary>
    public UnitVec3 Min { get; set; }

    /// <summary>The corner opposite <see cref="Min"/>.</summary>
    public UnitVec3 Max { get; set; }

    /// <summary>Runs when a watched player crosses in. May call <c>ctx.Units.Spawn</c>.</summary>
    public Action<AreaTriggerHit>? OnEnter { get; set; }

    /// <summary>Runs when a watched player crosses out, or disconnects while inside.</summary>
    public Action<AreaTriggerHit>? OnLeave { get; set; }

    /// <summary>Null when <see cref="PluginTriggers.Watch"/> can register this. Otherwise the reason it will throw.</summary>
    public string? Refusal()
    {
        if (OnEnter == null && OnLeave == null)
            return "Set OnEnter, OnLeave, or both.";
        if (Shape != AreaTriggerShape.Circle && Shape != AreaTriggerShape.Rectangle)
            return "Shape must be Circle or Rectangle.";
        if (Shape == AreaTriggerShape.Rectangle)
            return RectangleRefusal();

        if (!FiniteNonNegative(Radius))
            return "Radius must be zero or greater, and finite.";
        if (!FinitePositive(Height) || !float.IsFinite(Height * 0.5f))
            return "Height must be greater than zero, and finite.";
        if (!Finite(Center))
            return "Center must be a finite position.";
        return null;
    }

    internal void ReadRectangle(out UnitVec3 min, out UnitVec3 max)
    {
        min = new UnitVec3(
            MathF.Min(Min.X, Max.X),
            MathF.Min(Min.Y, Max.Y),
            MathF.Min(Min.Z, Max.Z));
        max = new UnitVec3(
            MathF.Max(Min.X, Max.X),
            MathF.Max(Min.Y, Max.Y),
            MathF.Max(Min.Z, Max.Z));
    }

    private string? RectangleRefusal()
    {
        if (!Finite(Min) || !Finite(Max))
            return "Min and Max must be finite positions.";
        float sizeX = MathF.Abs(Max.X - Min.X);
        float sizeY = MathF.Abs(Max.Y - Min.Y);
        float sizeZ = MathF.Abs(Max.Z - Min.Z);
        if (!(sizeX > 0f) || !(sizeY > 0f) || !(sizeZ > 0f)
            || !float.IsFinite(sizeX) || !float.IsFinite(sizeY) || !float.IsFinite(sizeZ))
            return "The rectangle must have a positive size on each axis.";
        ReadRectangle(out UnitVec3 min, out UnitVec3 max);
        float centerX = (min.X + max.X) * 0.5f;
        float centerY = (min.Y + max.Y) * 0.5f;
        float centerZ = (min.Z + max.Z) * 0.5f;
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY) || !float.IsFinite(centerZ))
            return "The rectangle must have a positive size on each axis.";
        return null;
    }

    private static bool Finite(UnitVec3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool FiniteNonNegative(float value) => float.IsFinite(value) && value >= 0f;

    private static bool FinitePositive(float value) => float.IsFinite(value) && value > 0f;
}

/// <summary>
/// One live area watch. <see cref="Dispose"/> stops it. Unload disposes it too.
/// A client registration is inactive and does nothing.
/// </summary>
public sealed class AreaTrigger : IDisposable
{
    private Action? _stop;

    /// <summary>False after <see cref="Dispose"/>, and false for a client registration.</summary>
    public bool IsActive { get; private set; }

    internal AreaTrigger(Action stop)
    {
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        IsActive = true;
    }

    private AreaTrigger()
    {
    }

    internal static AreaTrigger Inactive() => new();

    /// <summary>Stop watching. A second call does nothing.</summary>
    public void Dispose()
    {
        IsActive = false;
        var stop = _stop;
        _stop = null;
        stop?.Invoke();
    }
}

/// <summary>
/// Game-free enter/leave book. The server tick only supplies player positions.
/// The first position seen for a player records which side they are on and fires nothing,
/// so a plugin reload, a world load, or a reconnect does not spawn a wave under someone
/// already standing in the area. After that, crossing the volume fires. Leaving the server
/// while inside fires leave and forgets them.
/// </summary>
internal sealed class AreaTriggerBook
{
    internal readonly struct Sighting
    {
        public readonly ulong AccountId;
        public readonly string Name;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly bool HasPosition;

        public Sighting(ulong accountId, string? name, float x, float y, float z, bool hasPosition)
        {
            AccountId = accountId;
            Name = name ?? "";
            X = x;
            Y = y;
            Z = z;
            HasPosition = hasPosition;
        }
    }

    internal readonly struct Edge
    {
        public readonly int Id;
        public readonly ulong AccountId;
        public readonly string Name;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly bool Enter;

        public Edge(int id, ulong accountId, string name, float x, float y, float z, bool enter)
        {
            Id = id;
            AccountId = accountId;
            Name = name ?? "";
            X = x;
            Y = y;
            Z = z;
            Enter = enter;
        }
    }

    internal struct Watch
    {
        public int Id;
        public AreaTriggerShape Shape;
        public float X;
        public float Y;
        public float Z;
        public float Radius;
        public float HalfHeight;
        public float MinX;
        public float MinY;
        public float MinZ;
        public float MaxX;
        public float MaxY;
        public float MaxZ;
        public bool ReportEnter;
        public bool ReportLeave;
    }

    private struct Occ
    {
        public bool Inside;
        public float X;
        public float Y;
        public float Z;
        public string Name;
    }

    private readonly struct Key : IEquatable<Key>
    {
        public readonly int Id;
        public readonly ulong Account;

        public Key(int id, ulong account)
        {
            Id = id;
            Account = account;
        }

        public bool Equals(Key other) => Id == other.Id && Account == other.Account;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Id, Account);
    }

    private readonly List<Watch> _watches = new();
    private readonly Dictionary<Key, Occ> _occ = new();
    private readonly List<Sighting> _rows = new();
    private readonly Dictionary<ulong, int> _at = new();
    private readonly List<Key> _gone = new();
    private static readonly Comparison<Key> GoneOrder = CompareGone;
    private int _nextId;
    private int _epoch = int.MinValue;

    internal int WatchCount => _watches.Count;

    internal int OccupancyCount => _occ.Count;

    internal int Add(Watch watch)
    {
        watch.Id = ++_nextId;
        _watches.Add(watch);
        return watch.Id;
    }

    internal bool Remove(int id)
    {
        for (int i = 0; i < _watches.Count; i++)
        {
            if (_watches[i].Id != id) continue;
            _watches.RemoveAt(i);
            Drop(id);
            return true;
        }

        return false;
    }

    internal void Step(int epoch, IReadOnlyList<Sighting> sightings, List<Edge> edges)
    {
        if (sightings == null) throw new ArgumentNullException(nameof(sightings));
        if (edges == null) throw new ArgumentNullException(nameof(edges));
        edges.Clear();

        if (epoch != _epoch)
        {
            _occ.Clear();
            _epoch = epoch;
        }

        if (_watches.Count == 0) return;

        Compact(sightings);

        for (int w = 0; w < _watches.Count; w++)
        {
            Watch watch = _watches[w];
            for (int r = 0; r < _rows.Count; r++)
                Apply(watch, _rows[r], edges);
        }

        _gone.Clear();
        foreach (var pair in _occ)
        {
            if (!_at.ContainsKey(pair.Key.Account))
                _gone.Add(pair.Key);
        }

        _gone.Sort(GoneOrder);
        for (int i = 0; i < _gone.Count; i++)
        {
            Key key = _gone[i];
            if (!_occ.TryGetValue(key, out Occ occ)) continue;
            _occ.Remove(key);
            if (!occ.Inside) continue;
            if (!TryWatch(key.Id, out Watch watch) || !watch.ReportLeave) continue;
            edges.Add(new Edge(key.Id, key.Account, occ.Name, occ.X, occ.Y, occ.Z, enter: false));
        }
    }

    private void Apply(Watch watch, Sighting row, List<Edge> edges)
    {
        if (!row.HasPosition) return;

        var key = new Key(watch.Id, row.AccountId);
        bool core = InVolume(row.X, row.Y, row.Z, watch, AreaTriggerOptions.LeaveSkin, hold: false);
        if (!_occ.TryGetValue(key, out Occ occ))
        {
            _occ[key] = new Occ { Inside = core, X = row.X, Y = row.Y, Z = row.Z, Name = row.Name };
            return;
        }

        bool nowInside = occ.Inside
            ? InVolume(row.X, row.Y, row.Z, watch, AreaTriggerOptions.LeaveSkin, hold: true)
            : core;
        occ.Name = row.Name;
        occ.X = row.X;
        occ.Y = row.Y;
        occ.Z = row.Z;
        if (nowInside == occ.Inside)
        {
            _occ[key] = occ;
            return;
        }

        occ.Inside = nowInside;
        _occ[key] = occ;
        if (nowInside && !watch.ReportEnter) return;
        if (!nowInside && !watch.ReportLeave) return;
        edges.Add(new Edge(watch.Id, row.AccountId, row.Name, row.X, row.Y, row.Z, nowInside));
    }

    private static bool InVolume(float x, float y, float z, Watch watch, float skin, bool hold)
    {
        if (watch.Shape == AreaTriggerShape.Rectangle)
            return InRectangle(x, y, z, watch, skin, hold);
        if (watch.Shape == AreaTriggerShape.Circle)
            return InCircle(x, y, z, watch, skin, hold);
        return false;
    }

    private static bool InCircle(float x, float y, float z, Watch watch, float skin, bool hold)
    {
        float radius = watch.Radius;
        float half = watch.HalfHeight;
        if (!(radius >= 0f) || !float.IsFinite(radius)) return false;
        if (!(half > 0f) || !float.IsFinite(half)) return false;
        if (hold)
        {
            radius += skin;
            half += skin;
            if (!float.IsFinite(radius) || !float.IsFinite(half)) return false;
        }

        float dx = x - watch.X;
        float dz = z - watch.Z;
        if (dx * dx + dz * dz > radius * radius) return false;
        return MathF.Abs(y - watch.Y) <= half;
    }

    private static bool InRectangle(float x, float y, float z, Watch watch, float skin, bool hold)
    {
        float minX = MathF.Min(watch.MinX, watch.MaxX);
        float maxX = MathF.Max(watch.MinX, watch.MaxX);
        float minY = MathF.Min(watch.MinY, watch.MaxY);
        float maxY = MathF.Max(watch.MinY, watch.MaxY);
        float minZ = MathF.Min(watch.MinZ, watch.MaxZ);
        float maxZ = MathF.Max(watch.MinZ, watch.MaxZ);
        if (!FiniteSpan(minX, maxX) || !FiniteSpan(minY, maxY) || !FiniteSpan(minZ, maxZ))
            return false;
        if (hold)
        {
            minX -= skin;
            maxX += skin;
            minY -= skin;
            maxY += skin;
            minZ -= skin;
            maxZ += skin;
            if (!float.IsFinite(minX) || !float.IsFinite(maxX)
                || !float.IsFinite(minY) || !float.IsFinite(maxY)
                || !float.IsFinite(minZ) || !float.IsFinite(maxZ))
                return false;
        }

        return x >= minX && x <= maxX && y >= minY && y <= maxY && z >= minZ && z <= maxZ;
    }

    private static bool FiniteSpan(float min, float max) =>
        float.IsFinite(min) && float.IsFinite(max) && max > min;

    private void Compact(IReadOnlyList<Sighting> sightings)
    {
        _rows.Clear();
        _at.Clear();
        for (int i = 0; i < sightings.Count; i++)
        {
            Sighting row = sightings[i];
            if (row.AccountId == 0) continue;
            bool readable = row.HasPosition
                && float.IsFinite(row.X) && float.IsFinite(row.Y) && float.IsFinite(row.Z);
            var stored = new Sighting(row.AccountId, row.Name, row.X, row.Y, row.Z, readable);
            if (!_at.TryGetValue(row.AccountId, out int index))
            {
                _at[row.AccountId] = _rows.Count;
                _rows.Add(stored);
                continue;
            }

            if (!_rows[index].HasPosition && stored.HasPosition)
                _rows[index] = stored;
        }
    }

    private void Drop(int id)
    {
        _gone.Clear();
        foreach (var pair in _occ)
        {
            if (pair.Key.Id == id)
                _gone.Add(pair.Key);
        }

        for (int i = 0; i < _gone.Count; i++)
            _occ.Remove(_gone[i]);
    }

    private bool TryWatch(int id, out Watch watch)
    {
        for (int i = 0; i < _watches.Count; i++)
        {
            if (_watches[i].Id != id) continue;
            watch = _watches[i];
            return true;
        }

        watch = default;
        return false;
    }

    private static int CompareGone(Key a, Key b)
    {
        int byWatch = a.Id.CompareTo(b.Id);
        return byWatch != 0 ? byWatch : a.Account.CompareTo(b.Account);
    }
}
