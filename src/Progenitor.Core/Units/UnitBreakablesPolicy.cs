using System;

namespace Progenitor.Units;

/// <summary>
/// Game-free area rule for <see cref="PluginUnits.ClearBreakables"/>. Unit-tested without the game.
/// </summary>
public static class UnitBreakablesPolicy
{
    /// <summary>How far above or below the center a prop still counts: one floor of a building.</summary>
    public const float HeightBand = 3f;

    /// <summary>Inside the circle of <paramref name="radius"/> round the center (x, z), within <see cref="HeightBand"/> of its height.</summary>
    public static bool InArea(UnitVec3 prop, UnitVec3 center, float radius)
    {
        if (!(radius >= 0f) || float.IsInfinity(radius)) return false;
        float dx = prop.X - center.X, dz = prop.Z - center.Z;
        return dx * dx + dz * dz <= radius * radius && Math.Abs(prop.Y - center.Y) <= HeightBand;
    }
}
