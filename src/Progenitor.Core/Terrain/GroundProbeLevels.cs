using System;

namespace Progenitor.Terrain;

/// <summary>
/// How the game's height read turns a probe height into a height level. Game-free; compiled into the test project.
///
/// <para><c>TileWorld.TryGetGameplayHeight(in float3, out float, bool)</c> does not search down from the probe height.
/// It turns the probe's Y into a level, <c>level = clamp((y + 1 - (-50)) / 5, 0, 20)</c> truncated to a byte (NaN gives
/// 20), and reads the gameplay height stored in the tile cell's layer for that level. The cell clamps the layer index
/// to the layers it has (<c>TileCell.GetClampedTileLayerIndex</c>: <c>level - BaseHeightLevel</c>, clamped to 0 and to
/// the last layer), so a probe above the top layer reads the top layer and one below the cell's base reads its lowest.
/// The stored height decodes as <c>ushort * 0.002 - 50</c>.</para>
///
/// <para>Source: the game's native method bodies for <c>TileWorld.TryGetGameplayHeight</c> (the <c>float3</c>,
/// <c>float2</c>+level and <c>int2</c>+level overloads), <c>TileWorld.TryGetCellTileLayer</c> and
/// <c>TileCell.GetClampedTileLayerIndex</c> / <c>TryGetTileLayerFromHeightLevel</c> (server build of 2026-10-19), and
/// the interop metadata constants <c>TileLayerUtility.HeightLevelOffset = 1</c>, <c>MinHeightLevel = 0</c>,
/// <c>MaxHeightLevel = 20</c>. The 5 m level size and the -50 base are literals in those bodies; no metadata constant
/// names them. Corroborated live: a castle heart and floors at y 5 carry <c>TilePosition.HeightLevel</c> 11, which is
/// <see cref="LevelOf"/>(5). Notes: the internal research notes, <c>world-map-reveal-teleport.md</c>.</para>
///
/// <para>Consequence for a floor walk: probing 1 m under a floor found at 15.5 asks y 14.5, which is level 13 again
/// (the roof's own level), so the read returns the roof. A walk has to step a whole level at a time.</para>
/// </summary>
internal static class GroundProbeLevels
{
    /// <summary>Meters per height level (the divisor in the level formula).</summary>
    public const float LevelHeight = 5f;

    /// <summary>Height of level 0's lower edge in the offset frame (the <c>- -50</c> in the formula).</summary>
    public const float LevelBase = -50f;

    /// <summary>Added to the probe Y before it is turned into a level (<c>TileLayerUtility.HeightLevelOffset</c>).</summary>
    public const float QueryOffset = 1f;

    /// <summary><c>TileLayerUtility.MinHeightLevel</c>.</summary>
    public const int MinLevel = 0;

    /// <summary><c>TileLayerUtility.MaxHeightLevel</c>.</summary>
    public const int MaxLevel = 20;

    /// <summary>
    /// The level the game's height read uses for a probe at <paramref name="y"/>, exactly as the native body computes
    /// it: <c>(y + 1 + 50) / 5</c>, NaN or above 20 gives 20, below 0 gives 0, then truncated.
    /// </summary>
    public static int LevelOf(float y)
    {
        float f = (y + QueryOffset - LevelBase) / LevelHeight;
        if (float.IsNaN(f) || f > MaxLevel) f = MaxLevel;
        if (f < MinLevel) f = MinLevel;
        return (int)f;
    }

    /// <summary>
    /// A probe height that reads exactly <paramref name="level"/>: the middle of its 5 m band, so float rounding at a
    /// band edge cannot read the level above or below. <c>LevelOf(ProbeYFor(n)) == n</c> for every level 0 to 20.
    /// </summary>
    public static float ProbeYFor(int level)
    {
        level = Math.Max(MinLevel, Math.Min(MaxLevel, level));
        return LevelBase - QueryOffset + (level + 0.5f) * LevelHeight;
    }
}
