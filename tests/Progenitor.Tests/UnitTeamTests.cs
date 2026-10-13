using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>ctx.Units.JoinTeam</c> / <c>UnitSpawnOptions.TeamOf</c>, game-free half (<see cref="UnitTeamRules"/>):
/// what is copied, what is kept, when the unit already sits on the team, and the refusals. The live half
/// (<c>UnitTeams.cs</c>) reads and pokes <c>Team</c> and <c>TeamReference</c> at the dump offsets.
/// </summary>
public class UnitTeamTests
{
    // Live 2026-09-25: an API unit on Faction_Players read Team.Value 2 and the unit-team entity; a
    // player read Team.Value 11 and their own team entity.
    private static readonly UnitTeamState Skeleton = new(2, 7, new UnitId(500, 1));
    private static readonly UnitTeamState Player = new(11, 3, new UnitId(900, 4));

    [Fact]
    public void Team_constants_match_the_dump()
    {
        Assert.Equal(2, UnitTeamRules.UnitTeam);
        Assert.Equal(1, UnitTeamRules.NeutralTeam);
        Assert.Equal(0, Layout.TeamValueOffset);
        Assert.Equal(4, Layout.TeamFactionIndexOffset);
        Assert.Equal(0, Layout.TeamReferenceValueOffset);
        Assert.Equal(0, Layout.ModifiableEntityValueOffset);
    }

    [Fact]
    public void Joining_copies_value_and_reference_and_keeps_the_units_faction()
    {
        var joined = UnitTeamRules.Joined(Skeleton, Player);
        Assert.Equal(11, joined.Value);
        Assert.Equal(new UnitId(900, 4), joined.Reference);
        Assert.Equal(7, joined.FactionIndex);
        Assert.True(UnitTeamRules.IsOnTeamOf(joined, Player));
    }

    [Fact]
    public void A_unit_is_on_the_team_only_when_value_and_reference_both_match()
    {
        Assert.False(UnitTeamRules.IsOnTeamOf(Skeleton, Player));
        Assert.False(UnitTeamRules.IsOnTeamOf(new UnitTeamState(11, 7, Skeleton.Reference), Player));
        Assert.False(UnitTeamRules.IsOnTeamOf(new UnitTeamState(2, 7, Player.Reference), Player));
        // The faction index is not part of the team.
        Assert.True(UnitTeamRules.IsOnTeamOf(new UnitTeamState(11, 99, Player.Reference), Player));
    }

    [Fact]
    public void Refusals_name_the_problem()
    {
        var unit = new UnitId(10, 1);
        var player = new UnitId(20, 1);
        Assert.Null(UnitTeamRules.Refusal(unit, player, true, true, true, true, true));
        Assert.Contains("Entity.Null", UnitTeamRules.Refusal(unit, UnitId.None, false, false, false, true, true));
        Assert.Contains("its own team", UnitTeamRules.Refusal(unit, unit, true, true, true, true, true));
        Assert.Contains("does not exist", UnitTeamRules.Refusal(unit, player, false, false, false, true, true));
        Assert.Contains("character", UnitTeamRules.Refusal(unit, player, true, true, false, true, true));
        Assert.Contains("character", UnitTeamRules.Refusal(unit, player, true, false, true, true, true));
        Assert.Contains("unit", UnitTeamRules.Refusal(unit, player, true, true, true, false, true));
        Assert.Contains("unit", UnitTeamRules.Refusal(unit, player, true, true, true, true, false));
        // A ModifyTeamBuff buff owns TeamReference; the registry would restore the old team when it ends.
        Assert.Contains("ModifyTeamBuff", UnitTeamRules.Refusal(unit, player, true, true, true, true, true, unitHasTeamModifyingBuff: true));
    }
}
