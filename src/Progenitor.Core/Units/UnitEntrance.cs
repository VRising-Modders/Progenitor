using System;

namespace Progenitor.Units;

/// <summary>
/// How a unit appears when it is spawned (<see cref="UnitSpawnOptions.Entrance"/>): a vanilla spawn
/// buff put on the unit right after spawn. Every client draws it natively.
/// </summary>
public enum UnitEntrance
{
    /// <summary>No entrance buff; the unit spawns the way its prefab does.</summary>
    None = 0,

    /// <summary>
    /// The skeleton crawls out of the ground: <c>Buff_SpawnBuff_Skeleton_EarthCrawl</c> (1820474368).
    /// Seen live on skeletons (2026-09-25 screenshots); other creatures are not checked.
    /// </summary>
    RiseFromGround = 1,
}

/// <summary>The entrance table: <see cref="UnitEntrance"/> to the vanilla buff GUID it applies.</summary>
internal static class UnitEntranceTable
{
    /// <summary><c>Buff_SpawnBuff_Skeleton_EarthCrawl</c>.</summary>
    internal const int RiseFromGroundBuff = 1820474368;

    internal static readonly (UnitEntrance Entrance, string Buff, int Guid)[] Rows =
    {
        (UnitEntrance.RiseFromGround, "Buff_SpawnBuff_Skeleton_EarthCrawl", RiseFromGroundBuff),
    };

    /// <summary>True for a named value, including <see cref="UnitEntrance.None"/>.</summary>
    internal static bool IsDefined(UnitEntrance entrance) => entrance == UnitEntrance.None || TryGetBuff(entrance, out _);

    /// <summary>The buff GUID for <paramref name="entrance"/>; false for None and undefined values.</summary>
    internal static bool TryGetBuff(UnitEntrance entrance, out int guid)
    {
        foreach (var row in Rows)
        {
            if (row.Entrance == entrance)
            {
                guid = row.Guid;
                return true;
            }
        }
        guid = 0;
        return false;
    }

    internal static string RangeMessage(UnitEntrance entrance) =>
        "unit entrance " + (int)entrance + " is not a defined UnitEntrance (None = 0, RiseFromGround = 1).";

    internal static void Validate(UnitEntrance entrance, string paramName)
    {
        if (!IsDefined(entrance)) throw new ArgumentOutOfRangeException(paramName, RangeMessage(entrance));
    }
}
