using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Servants;

/// <summary>
/// <see cref="PluginContext.Servants"/>: castle servants and their hunts on the server. Read a
/// castle's servants the way its throne lists them, read the heart's hunt record, send servants
/// on a hunt with the writes the game's own throne send makes (validated with the game's checks),
/// send a returned hunt again, and swap the throne's list for one player (experimental).
/// Returns are announced with <see cref="ServantsReturned"/> and Progenitor's sends with
/// <see cref="ServantsSent"/> on <c>ctx.Events</c>.
///
/// <para><b>Status: experimental.</b> Source-checked against the game's metadata and code and
/// game-free tested; not live-verified (<c>docs/modules.md</c>, Servants). Server only and main
/// thread only: on the client every member throws <see cref="InvalidOperationException"/>, except
/// <see cref="MissionDisplayName"/> (client only) and <see cref="MissionToken"/> (either side).</para>
/// </summary>
public sealed class PluginServants
{
    private readonly PluginContext _ctx;

    internal PluginServants(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// The servants a throne of this castle lists, in the game's order and with the state the
    /// throne shows. <paramref name="throneOrHeart"/> is the heart, the throne or anything else
    /// connected to the heart. Empty when it is none of those.
    /// </summary>
    public IReadOnlyList<ServantInfo> List(Entity throneOrHeart) => ServantRuntime.List(World(nameof(List)), throneOrHeart);

    /// <summary>The hunts in the castle heart's record, in the record's order.</summary>
    public IReadOnlyList<ServantMission> Missions(Entity throneOrHeart) => ServantRuntime.Missions(World(nameof(Missions)), throneOrHeart);

    /// <summary>The map zones whose servant mission is <paramref name="missionGuid"/> (usually one).</summary>
    public IReadOnlyList<MissionZone> ZonesOf(int missionGuid) =>
        ServantRuntime.ZonesFor(World(nameof(ZonesOf)).EntityManager, missionGuid);

    /// <summary>The servant mission of a map zone, 0 when the zone has none.</summary>
    public int MissionOf(MissionZone zone) => ServantRuntime.MissionFor(World(nameof(MissionOf)).EntityManager, zone);

    /// <summary>
    /// Send servants on a hunt. Validated with the game's own checks (servant alive, home, unhurt,
    /// empty inventory, of this heart; one hunt per zone and castle; the mission's slots; the hunt
    /// length choice; the zone discovered by the user) and refused with a reason; on success the
    /// heart's hunt record gets the row the game's send writes and each servant gets the mission
    /// buff, and the users allied with the castle get the game's "Hunt in X started." notice
    /// (<see cref="ServantSendRequest.Announce"/>). Never synthesizes the client's throne event;
    /// the result carries each servant's success chance (<see cref="ServantSendResult.Chances"/>),
    /// which the game rolls when the hunt returns.
    /// </summary>
    public ServantSendResult Send(ServantSendRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return ServantRuntime.Send(World(nameof(Send)), request, _ctx.Guid);
    }

    /// <summary>
    /// <see cref="Send(ServantSendRequest)"/> from entities of the current world load.
    /// <paramref name="user"/> (default: the heart's owner) is whose map discovery is checked.
    /// </summary>
    public ServantSendResult Send(Entity heart, IReadOnlyList<Entity> servants, int missionGuid, int dataSlot, Entity user = default, bool checkDiscovered = true)
    {
        if (servants == null) throw new ArgumentNullException(nameof(servants));
        var ids = new List<CastleId>(servants.Count);
        foreach (var s in servants) ids.Add(CastleId.From(s));
        return Send(new ServantSendRequest
        {
            Heart = CastleId.From(heart),
            Servants = ids,
            MissionGuid = missionGuid,
            DataSlot = dataSlot,
            User = CastleId.From(user),
            CheckDiscovered = checkDiscovered,
        });
    }

    /// <summary>
    /// Send the servants of a finished hunt that came back unhurt on the same zone, mission and hunt
    /// length from the same heart. Dead and injured servants stay home. A servant that still
    /// carries its loot is refused (<see cref="ServantSendRefusal.ServantHasItems"/>): move the loot
    /// out first with <c>ctx.Inventory.TransferAll(servant.Inventory, …)</c>. The zone discovery is
    /// not checked again (the game accepted it for the first send). A return from an earlier world
    /// load is refused.
    /// </summary>
    public ServantSendResult Resend(ServantsReturned returned)
    {
        if (returned == null) throw new ArgumentNullException(nameof(returned));
        return ServantRuntime.Resend(World(nameof(Resend)), returned, _ctx.Guid);
    }

    /// <summary>
    /// The success chance of a prospective send, per servant, as the game would roll it at the
    /// return and as the throne's hunt menu shows it (<see cref="ServantChance"/>), plus whether
    /// <see cref="Send(ServantSendRequest)"/> would accept the request now (the same checks, no
    /// writes). The chances are filled whenever they can be read, even when the send would be
    /// refused for another reason (a servant still carrying loot, for example).
    /// </summary>
    public ServantChanceResult Chance(ServantChanceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.MaxSuccess is float cap && !(cap >= 0f && cap <= 1f))
            throw new ArgumentOutOfRangeException(nameof(request), "MaxSuccess must be between 0 and 1");
        return ServantRuntime.Chance(World(nameof(Chance)), request, _ctx.Guid);
    }

    /// <summary>
    /// Client only: a mission's localized name as the game's own hunt notices print it
    /// (<c>ManagedMissionData.Name</c> through the game's string table), for example
    /// "Fishing Lake"; null when the id is not a servant mission, on the server (which has no string
    /// table), or before the client's game data is loaded. Main thread.
    /// </summary>
    public string? MissionDisplayName(int missionGuid)
    {
        _ctx.EnsureAttached();
        if (Plugin.IsServerProcess || missionGuid == 0) return null;
        return Progenitor.Chat.ChatGameData.MissionName(missionGuid);
    }

    /// <summary>
    /// The chat token <c>[mission:&lt;guid&gt;]</c> for a mission. Progenitor's chat shows it as the
    /// mission's localized name; a client without Progenitor shows the token as written, so send it
    /// only to players that run Progenitor, or write the name yourself for others. Either side.
    /// </summary>
    public string MissionToken(int missionGuid) => Progenitor.Chat.ChatTokens.Mission(missionGuid);

    /// <summary>
    /// Experimental. Register the one provider that chooses, per player, whose servants a castle
    /// throne lists. Throws <see cref="InvalidOperationException"/> naming the current owner when
    /// another plugin's provider is registered. The handle is tracked, so unload releases it.
    /// </summary>
    public IDisposable RegisterListProvider(IServantListProvider provider)
    {
        World(nameof(RegisterListProvider));
        var handle = ServantRuntime.Lists.Register(provider, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>The throne list hook: who registered and what it did. Server only.</summary>
    public ServantListStatus ListStatus
    {
        get
        {
            World(nameof(ListStatus));
            return ServantRuntime.Lists.Status();
        }
    }

    private World World(string op)
    {
        _ctx.EnsureAttached();
        return ServantRuntime.RequireWorld(op);
    }
}
