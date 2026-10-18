using System.Collections.Generic;

namespace Progenitor.Dev.Ops;

/// <summary>What <see cref="OfflineUserTeamRules.Decide"/> tells the live helper to do.</summary>
internal enum OfflineUserTeamAction
{
    /// <summary>The user already has its own team: nothing to write.</summary>
    Ready,
    /// <summary>Instantiate the player team prefab and link it to the user.</summary>
    Create,
    /// <summary>Do not touch the user (<see cref="OfflineUserTeamDecision.Code"/> says why).</summary>
    Refuse,
}

/// <summary>The rule's answer: an action and, for a refusal, a short code and a sentence.</summary>
internal readonly struct OfflineUserTeamDecision
{
    public OfflineUserTeamDecision(OfflineUserTeamAction action, string code, string detail)
    {
        Action = action;
        Code = code;
        Detail = detail;
    }

    public OfflineUserTeamAction Action { get; }
    public string Code { get; }
    public string Detail { get; }
}

/// <summary>What the live side read about a user and the entity its <c>TeamReference</c> names.</summary>
internal sealed class OfflineUserTeamObservation
{
    public bool UserExists { get; set; }
    public bool HasUser { get; set; }
    public bool IsConnected { get; set; }
    public bool HasTeamReference { get; set; }
    /// <summary>The <c>TeamReference</c> names an entity that exists (false for <c>Entity.Null</c>).</summary>
    public bool TeamExists { get; set; }
    public bool TeamHasUserTeam { get; set; }
    /// <summary>That team's <c>UserTeam.UserEntity</c> is this user.</summary>
    public bool TeamPointsAtUser { get; set; }
    public bool TeamHasAllies { get; set; }
}

/// <summary>What the live side read about a castle heart and the user that would claim it.</summary>
internal sealed class ClaimCastleObservation
{
    public bool UserHasTeamReference { get; set; }
    /// <summary>The user's team exists and has a <c>TeamAllies</c> buffer.</summary>
    public bool UserTeamReady { get; set; }
    public bool HeartExists { get; set; }
    public bool HeartHasCastleHeart { get; set; }
    public bool HeartHasTeamReference { get; set; }
    /// <summary>The heart's team (the castle team) exists and has a <c>TeamAllies</c> buffer.</summary>
    public bool CastleTeamReady { get; set; }
    public int CastleTeamAllies { get; set; }
    /// <summary>When the castle team has one ally (the previous owner's team): it has <c>TeamAllies</c> and <c>UserTeam</c>.</summary>
    public bool PreviousTeamReady { get; set; }
}

/// <summary>
/// Game-free rules for giving an offline Dev user (a <c>stress_populate</c> owner) a team of its own,
/// and for checking that <c>TeamUtility.ClaimCastle</c> could run for it. Both mirror the server's
/// own code (research note <c>stress-owner-team-2026-10-18.md</c>):
///
/// <para><b>New user team.</b> When a new user connects for the first time, the server instantiates
/// <c>ServerRootPrefabCollection.PlayerTeamPrefabGUID</c>, sets that team's <c>UserTeam.UserEntity</c> to
/// the user, and sets the user's <c>TeamReference</c> base value to the team. No team event is created.</para>
///
/// <para><b>ClaimCastle reads.</b> <c>TeamReference</c> on the user and on the heart (both required);
/// <c>TeamAllies</c> on the user's team and the heart's castle team (both required); a castle team with
/// one ally is a previous owner's team, which needs <c>TeamAllies</c> and <c>UserTeam</c>. It expects at
/// most one ally on the castle team. It then edits both ally lists, recounts the user's hearts, writes
/// the heart's <c>UserOwner</c> and <c>CastleHeart.LastUserOwner</c>, and creates a <c>ChangedTeamEvent</c>
/// and a <c>RefreshTeamEvent</c>.</para>
/// </summary>
internal static class OfflineUserTeamRules
{
    public static OfflineUserTeamDecision Decide(OfflineUserTeamObservation o)
    {
        if (o == null || !o.UserExists)
            return Refuse("no_user", "the user entity does not exist");
        if (!o.HasUser)
            return Refuse("no_user", "the entity has no User component");
        if (o.IsConnected)
            return Refuse("connected", "the user is connected; the game gave it a team when it joined");
        if (!o.HasTeamReference || !o.TeamExists)
            return new OfflineUserTeamDecision(OfflineUserTeamAction.Create, "", "");
        if (!o.TeamHasUserTeam)
            return Refuse("foreign_team", "the user's TeamReference names an entity that is not a user team");
        if (!o.TeamPointsAtUser)
            return Refuse("foreign_team", "the user's TeamReference names another user's team");
        if (!o.TeamHasAllies)
            return Refuse("team_without_allies", "the user's team has no TeamAllies buffer");
        return new OfflineUserTeamDecision(OfflineUserTeamAction.Ready, "", "");
    }

    /// <summary>Null when every read ClaimCastle makes would succeed; otherwise the first missing piece.</summary>
    public static string? ClaimCastleRefusal(ClaimCastleObservation c)
    {
        if (c == null) return "nothing was read";
        if (!c.UserHasTeamReference) return "the user has no TeamReference (run the owner team step first)";
        if (!c.UserTeamReady) return "the user's team does not exist or has no TeamAllies";
        if (!c.HeartExists) return "the heart does not exist";
        if (!c.HeartHasCastleHeart) return "the entity is not a castle heart";
        if (!c.HeartHasTeamReference) return "the heart has no TeamReference";
        if (!c.CastleTeamReady) return "the heart's castle team does not exist yet or has no TeamAllies";
        if (c.CastleTeamAllies > 1) return "the castle team has " + c.CastleTeamAllies + " allies; ClaimCastle expects at most one";
        if (c.CastleTeamAllies == 1 && !c.PreviousTeamReady) return "the castle team's ally (the previous owner's team) has no TeamAllies or UserTeam";
        return null;
    }

    /// <summary>The team entities a destroy of this user should also remove: its own team only.</summary>
    public static bool IsOwnTeam(bool teamExists, bool teamHasUserTeam, bool teamPointsAtUser) =>
        teamExists && teamHasUserTeam && teamPointsAtUser;

    /// <summary>Short labels for a read-back row, in the order <see cref="Decide"/> checks them.</summary>
    public static IReadOnlyList<string> Missing(OfflineUserTeamObservation o)
    {
        var list = new List<string>();
        if (o == null) { list.Add("user"); return list; }
        if (!o.UserExists || !o.HasUser) list.Add("user");
        if (!o.HasTeamReference) list.Add("TeamReference");
        else if (!o.TeamExists) list.Add("team");
        else
        {
            if (!o.TeamHasUserTeam) list.Add("UserTeam");
            else if (!o.TeamPointsAtUser) list.Add("UserTeam.UserEntity");
            if (!o.TeamHasAllies) list.Add("TeamAllies");
        }
        return list;
    }

    private static OfflineUserTeamDecision Refuse(string code, string detail) =>
        new(OfflineUserTeamAction.Refuse, code, detail);
}
