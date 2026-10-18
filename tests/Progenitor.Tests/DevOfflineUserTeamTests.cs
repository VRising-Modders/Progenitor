using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of <c>GhostOps.EnsureOfflineUserTeam</c> and the <c>stress_team</c> claim
/// check: when an offline user gets a new team, when it is left alone or refused, and which
/// reads of <c>TeamUtility.ClaimCastle</c> would fail.
/// </summary>
public class DevOfflineUserTeamTests
{
    private static OfflineUserTeamObservation Bare() => new() { UserExists = true, HasUser = true };

    private static OfflineUserTeamObservation Own() => new()
    {
        UserExists = true,
        HasUser = true,
        HasTeamReference = true,
        TeamExists = true,
        TeamHasUserTeam = true,
        TeamPointsAtUser = true,
        TeamHasAllies = true,
    };

    [Fact]
    public void A_bare_offline_user_gets_a_new_team()
    {
        var d = OfflineUserTeamRules.Decide(Bare());
        Assert.Equal(OfflineUserTeamAction.Create, d.Action);
        Assert.Equal(new[] { "TeamReference" }, OfflineUserTeamRules.Missing(Bare()));
    }

    [Fact]
    public void A_null_or_dangling_team_reference_also_gets_a_new_team()
    {
        var o = Bare();
        o.HasTeamReference = true;
        Assert.Equal(OfflineUserTeamAction.Create, OfflineUserTeamRules.Decide(o).Action);
        Assert.Equal(new[] { "team" }, OfflineUserTeamRules.Missing(o));
    }

    [Fact]
    public void A_user_with_its_own_team_is_left_alone()
    {
        Assert.Equal(OfflineUserTeamAction.Ready, OfflineUserTeamRules.Decide(Own()).Action);
        Assert.Empty(OfflineUserTeamRules.Missing(Own()));
    }

    [Fact]
    public void Missing_or_connected_users_are_refused()
    {
        Assert.Equal("no_user", OfflineUserTeamRules.Decide(new OfflineUserTeamObservation()).Code);
        Assert.Equal("no_user", OfflineUserTeamRules.Decide(new OfflineUserTeamObservation { UserExists = true }).Code);
        var connected = Bare();
        connected.IsConnected = true;
        var d = OfflineUserTeamRules.Decide(connected);
        Assert.Equal(OfflineUserTeamAction.Refuse, d.Action);
        Assert.Equal("connected", d.Code);
    }

    [Fact]
    public void Someone_elses_team_is_never_overwritten()
    {
        var notUserTeam = Own();
        notUserTeam.TeamHasUserTeam = false;
        Assert.Equal("foreign_team", OfflineUserTeamRules.Decide(notUserTeam).Code);

        var otherUser = Own();
        otherUser.TeamPointsAtUser = false;
        Assert.Equal("foreign_team", OfflineUserTeamRules.Decide(otherUser).Code);
        Assert.Contains("UserTeam.UserEntity", OfflineUserTeamRules.Missing(otherUser));

        var noAllies = Own();
        noAllies.TeamHasAllies = false;
        Assert.Equal("team_without_allies", OfflineUserTeamRules.Decide(noAllies).Code);
    }

    [Fact]
    public void Only_the_users_own_team_is_destroyed_with_it()
    {
        Assert.True(OfflineUserTeamRules.IsOwnTeam(true, true, true));
        Assert.False(OfflineUserTeamRules.IsOwnTeam(false, true, true));
        Assert.False(OfflineUserTeamRules.IsOwnTeam(true, false, true));
        Assert.False(OfflineUserTeamRules.IsOwnTeam(true, true, false));
    }

    private static ClaimCastleObservation Claimable(int castleAllies = 0) => new()
    {
        UserHasTeamReference = true,
        UserTeamReady = true,
        HeartExists = true,
        HeartHasCastleHeart = true,
        HeartHasTeamReference = true,
        CastleTeamReady = true,
        CastleTeamAllies = castleAllies,
        PreviousTeamReady = castleAllies == 1,
    };

    [Fact]
    public void ClaimCastle_reads_succeed_for_an_unowned_or_singly_owned_heart()
    {
        Assert.Null(OfflineUserTeamRules.ClaimCastleRefusal(Claimable()));
        Assert.Null(OfflineUserTeamRules.ClaimCastleRefusal(Claimable(1)));
    }

    [Fact]
    public void ClaimCastle_refusals_name_the_first_missing_piece()
    {
        var noTeam = Claimable();
        noTeam.UserHasTeamReference = false;
        Assert.Contains("owner team step", OfflineUserTeamRules.ClaimCastleRefusal(noTeam));

        var userTeam = Claimable();
        userTeam.UserTeamReady = false;
        Assert.Contains("user's team", OfflineUserTeamRules.ClaimCastleRefusal(userTeam));

        var notHeart = Claimable();
        notHeart.HeartHasCastleHeart = false;
        Assert.Contains("not a castle heart", OfflineUserTeamRules.ClaimCastleRefusal(notHeart));

        var noCastleTeam = Claimable();
        noCastleTeam.CastleTeamReady = false;
        Assert.Contains("castle team does not exist", OfflineUserTeamRules.ClaimCastleRefusal(noCastleTeam));

        Assert.Contains("at most one", OfflineUserTeamRules.ClaimCastleRefusal(Claimable(2)));

        var brokenPrevious = Claimable(1);
        brokenPrevious.PreviousTeamReady = false;
        Assert.Contains("previous owner", OfflineUserTeamRules.ClaimCastleRefusal(brokenPrevious));

        Assert.Equal("nothing was read", OfflineUserTeamRules.ClaimCastleRefusal(null!));
    }
}
