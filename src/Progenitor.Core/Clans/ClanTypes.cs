using System;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Clans;

/// <summary>
/// One clan as a mod sees it. The entity id is session-scoped; <see cref="ClanGuid"/>
/// (<c>ClanTeam.ClanGuid</c>, saved with the world) is the key to persist.
/// </summary>
public readonly struct ClanRef : IEquatable<ClanRef>
{
    /// <summary>Construct a clan reference.</summary>
    public ClanRef(CastleId entity, Guid clanGuid, string name)
    {
        Entity = entity;
        ClanGuid = clanGuid;
        _name = name ?? "";
    }

    /// <summary>The clan entity in the current world load.</summary>
    public CastleId Entity { get; }

    /// <summary><c>ClanTeam.ClanGuid</c>: survives restarts.</summary>
    public Guid ClanGuid { get; }

    /// <summary><c>ClanTeam.Name</c>.</summary>
    public string Name => _name ?? "";
    private readonly string? _name;

    /// <summary>True when the user is in no clan.</summary>
    public bool IsNone => Entity.IsNone;

    /// <summary>No clan.</summary>
    public static ClanRef None => default;

    /// <summary>Same clan entity.</summary>
    public bool Equals(ClanRef other) => Entity == other.Entity;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ClanRef o && Equals(o);

    /// <inheritdoc />
    public override int GetHashCode() => Entity.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => IsNone ? "no clan" : (Name.Length > 0 ? Name : "clan") + " " + Entity;
}

/// <summary>One member of a clan.</summary>
public sealed class ClanMember
{
    /// <summary>User entity in the current world load.</summary>
    public CastleId User { get; init; }

    /// <summary><c>User.PlatformId</c>: the account; persist this.</summary>
    public ulong PlatformId { get; init; }

    /// <summary><c>User.CharacterName</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary><c>User.IsConnected</c>.</summary>
    public bool IsConnected { get; init; }

    /// <inheritdoc />
    public override string ToString() => (Name.Length > 0 ? Name : User.ToString()) + (IsConnected ? " (online)" : "");
}

/// <summary>A user row the clan logic groups: what the server reads off each <c>User</c>.</summary>
internal readonly struct ClanUserRow
{
    /// <summary>Construct a row.</summary>
    public ClanUserRow(CastleId user, CastleId clan, ulong platformId, string name, bool connected)
    {
        User = user;
        Clan = clan;
        PlatformId = platformId;
        Name = name ?? "";
        IsConnected = connected;
    }

    /// <summary>User entity.</summary>
    public CastleId User { get; }

    /// <summary><c>User.ClanEntity</c>, none when clanless.</summary>
    public CastleId Clan { get; }

    /// <summary><c>User.PlatformId</c>.</summary>
    public ulong PlatformId { get; }

    /// <summary><c>User.CharacterName</c>.</summary>
    public string Name { get; }

    /// <summary><c>User.IsConnected</c>.</summary>
    public bool IsConnected { get; }
}

/// <summary>Pure clan logic, shared by the server runtime and the tests.</summary>
internal static class ClanLogic
{
    /// <summary>
    /// The members of <paramref name="clan"/>: every user whose <c>User.ClanEntity</c> is the
    /// clan, ordered by name then account. A user record with account 0 (not a real account) is
    /// skipped. Empty for <see cref="CastleId.None"/>.
    /// </summary>
    public static IReadOnlyList<ClanMember> Members(IEnumerable<ClanUserRow> users, CastleId clan)
    {
        if (clan.IsNone || users == null) return Array.Empty<ClanMember>();
        var rows = new List<ClanMember>();
        foreach (var u in users)
        {
            if (u.Clan != clan || u.PlatformId == 0) continue;
            rows.Add(new ClanMember { User = u.User, PlatformId = u.PlatformId, Name = u.Name, IsConnected = u.IsConnected });
        }

        rows.Sort(static (a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.PlatformId.CompareTo(b.PlatformId);
        });
        return rows;
    }

    /// <summary>
    /// How a user stands to a castle. <see cref="CastleRelation.Owner"/> when the user owns the
    /// heart; <see cref="CastleRelation.ClanMember"/> when the user's clan owns it (the owner's
    /// clan); <see cref="CastleRelation.Allied"/> when neither holds but the game's team check
    /// (<c>Team.IsAllies</c> between the user's character and the heart) says allied;
    /// otherwise <see cref="CastleRelation.None"/>.
    /// </summary>
    public static CastleRelation Relation(CastleId user, CastleId userClan, CastleOwnerInfo owner, bool? teamAllied)
    {
        if (user.IsNone || owner == null) return CastleRelation.None;
        if (!owner.User.IsNone && owner.User == user) return CastleRelation.Owner;
        if (!userClan.IsNone && owner.Clan == userClan) return CastleRelation.ClanMember;
        return teamAllied == true ? CastleRelation.Allied : CastleRelation.None;
    }

    /// <summary>True when the relation lets the user use the castle's containers the way the game allows allies.</summary>
    public static bool IsAllied(CastleRelation relation) => relation != CastleRelation.None;
}
