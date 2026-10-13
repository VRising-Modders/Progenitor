using System.Collections.Generic;

namespace Progenitor.Combat;

/// <summary>
/// Game-free rule for <c>CombatMusicRepairServer</c>: whether a player's combat music still has a unit behind it.
///
/// <para>The game sets a player's <c>CombatMusicListener_Shared.UnitPrefabGuid</c> when a music-carrying unit (a V Blood)
/// starts targeting them, but only while it is 0, and clears it when a unit stops targeting them, but only when that unit
/// is of the same prefab. Then it takes the first other targeting unit with active music, or 0. A boss that leaves
/// without that "stopped" event (for example, a player teleports away and the boss is parked out of range) leaves the
/// value set for good: the music plays out of combat, and no later fight can replace it. Live 2026-10-07: a player's
/// music named a boss that no longer existed in the world, with nothing targeting them.</para>
///
/// <para>This rule gives the value the game's own "stopped" step would leave, from the units targeting the player now.</para>
/// </summary>
internal static class CombatMusicRepair
{
    /// <summary>How often the server checks, in seconds.</summary>
    internal const double CheckSeconds = 2.0;

    /// <summary>
    /// The music a player should have: <paramref name="current"/> while a targeting unit of that prefab remains (active or
    /// not, so a fight in progress is never touched); else the first targeting unit with active music; else 0.
    /// </summary>
    internal static int Expected(int current, IReadOnlyList<(int Prefab, bool Active)> targeting)
    {
        if (current == 0) return 0;
        foreach (var t in targeting)
            if (t.Prefab == current) return current;
        foreach (var t in targeting)
            if (t.Active && t.Prefab != 0) return t.Prefab;
        return 0;
    }

    /// <summary>
    /// Whether to write <paramref name="expected"/> now: only when the same stale value was already seen on the previous
    /// check, so a value set in the same frame as the check is never raced.
    /// </summary>
    internal static bool ShouldRepair(int current, int expected, int? staleOnLastCheck) =>
        expected != current && staleOnLastCheck == current;
}
