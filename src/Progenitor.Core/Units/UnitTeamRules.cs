namespace Progenitor.Units;

/// <summary>
/// A unit's team as <c>ctx.Units.JoinTeam</c> reads and writes it: <c>Team.Value</c>,
/// <c>Team.FactionIndex</c> and the entity <c>TeamReference.Value</c> names. Game-free.
/// </summary>
internal readonly record struct UnitTeamState(int Value, int FactionIndex, UnitId Reference);

/// <summary>
/// Game-free rules for <c>ctx.Units.JoinTeam</c> and <see cref="UnitSpawnOptions.TeamOf"/>. The live half
/// (<c>UnitTeams</c>) reads both entities' <c>Team</c> and <c>TeamReference</c> at the dump offsets and
/// pokes the unit's.
///
/// <para><b>What is copied.</b> <c>Team.Value</c> and <c>TeamReference</c> from the character. The unit's
/// own <c>Team.FactionIndex</c> is kept: it is what the unit's AI and the <c>Faction</c> option use to pick
/// who it fights, and the live test that motivated this (2026-09-25) kept it and the army still fought
/// Dracula while the player's projectiles passed through it.</para>
/// </summary>
internal static class UnitTeamRules
{
    /// <summary><c>Team.UNIT_TEAM</c>: the team every API-spawned unit sits on.</summary>
    internal const int UnitTeam = 2;

    /// <summary><c>Team.NEUTRAL_TEAM</c>.</summary>
    internal const int NeutralTeam = 1;

    /// <summary>
    /// The state the unit should have after joining <paramref name="character"/>: the character's value
    /// and reference, the unit's own faction index.
    /// </summary>
    internal static UnitTeamState Joined(UnitTeamState unit, UnitTeamState character) =>
        new(character.Value, unit.FactionIndex, character.Reference);

    /// <summary>True when <paramref name="unit"/> already sits on <paramref name="character"/>'s team (value and reference).</summary>
    internal static bool IsOnTeamOf(UnitTeamState unit, UnitTeamState character) =>
        unit.Value == character.Value && unit.Reference == character.Reference;

    /// <summary>
    /// Why <paramref name="unit"/> cannot join <paramref name="character"/>'s team, or null. The character
    /// must be another, existing entity with both <c>Team</c> and <c>TeamReference</c>, and the unit must
    /// have both too. Refused while the unit wears a buff with <c>ModifyTeamBuff</c>: that buff's
    /// modification owns <c>TeamReference</c> (<c>ModifiableFieldId.Create&lt;TeamReference, ModifiableEntity&gt;</c>,
    /// <c>UpdateModifyTeamBuffSystem</c>) and the registry would put the old team
    /// back when it ends.
    /// </summary>
    internal static string? Refusal(UnitId unit, UnitId character, bool characterExists, bool characterHasTeam, bool characterHasReference, bool unitHasTeam, bool unitHasReference, bool unitHasTeamModifyingBuff = false)
    {
        if (character.IsNone) return "the character is Entity.Null.";
        if (character == unit) return "a unit cannot join its own team.";
        if (!characterExists) return "character " + character + " does not exist.";
        if (!characterHasTeam || !characterHasReference) return "character " + character + " has no Team and TeamReference to copy.";
        if (!unitHasTeam || !unitHasReference) return "unit " + unit + " has no Team and TeamReference to write.";
        if (unitHasTeamModifyingBuff) return "unit " + unit + " wears a buff with ModifyTeamBuff, which owns its TeamReference until it ends; join after it ends.";
        return null;
    }
}
