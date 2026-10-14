using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Castles;

namespace Progenitor.Players;

/// <summary>
/// Where one online player's character stood when <c>ctx.Players.OnlinePositions()</c> was called.
/// A snapshot, not a live view. Game-free.
/// </summary>
public readonly struct PlayerPosition
{
    /// <summary>Make one.</summary>
    public PlayerPosition(PlayerInfo player, float x, float y, float z)
    {
        Player = player ?? throw new ArgumentNullException(nameof(player));
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The directory row of the player (account, name, session refs).</summary>
    public PlayerInfo Player { get; }

    /// <summary>World X of the character (<c>Translation.Value.x</c>).</summary>
    public float X { get; }

    /// <summary>World Y: height in meters.</summary>
    public float Y { get; }

    /// <summary>World Z.</summary>
    public float Z { get; }

    /// <summary>The same point as the castle API's vector, for <c>ICastleHandle.IsInside</c>.</summary>
    public CastleVec3 Position => new(X, Y, Z);

    /// <summary>Horizontal (X/Z) distance to a point, the way the game's area checks measure.</summary>
    public float HorizontalDistanceTo(float x, float z) => PlayerPositionMath.Horizontal(X, Z, x, z);

    /// <summary><c>Name (account) @ x, y, z</c>.</summary>
    public override string ToString() =>
        (Player?.DisplayName ?? "") + " @ " + X.ToString("0.0", CultureInfo.InvariantCulture) + ", "
        + Y.ToString("0.0", CultureInfo.InvariantCulture) + ", " + Z.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>Game-free distance helpers over <see cref="PlayerPosition"/> lists.</summary>
public static class PlayerPositionMath
{
    /// <summary>Horizontal (X/Z) distance between two points.</summary>
    public static float Horizontal(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>True when at least one position is within <paramref name="radius"/> meters (horizontal, inclusive) of the point.</summary>
    public static bool AnyWithin(IReadOnlyList<PlayerPosition> positions, float x, float z, float radius)
    {
        if (positions == null) throw new ArgumentNullException(nameof(positions));
        for (int i = 0; i < positions.Count; i++)
            if (positions[i].HorizontalDistanceTo(x, z) <= radius) return true;
        return false;
    }

    /// <summary>The positions within <paramref name="radius"/> meters (horizontal, inclusive) of the point, nearest first.</summary>
    public static List<PlayerPosition> Within(IReadOnlyList<PlayerPosition> positions, float x, float z, float radius)
    {
        if (positions == null) throw new ArgumentNullException(nameof(positions));
        var hits = new List<PlayerPosition>();
        for (int i = 0; i < positions.Count; i++)
            if (positions[i].HorizontalDistanceTo(x, z) <= radius) hits.Add(positions[i]);
        hits.Sort((a, b) => a.HorizontalDistanceTo(x, z).CompareTo(b.HorizontalDistanceTo(x, z)));
        return hits;
    }
}
