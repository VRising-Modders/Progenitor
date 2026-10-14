using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Castles;
using Progenitor.Inventory;

namespace Progenitor.Servants;

/// <summary>
/// A servant's state the way the castle throne's list shows it. The numbers are the game's own
/// <c>ServantInfoEvent.Response.ServantState</c> values.
/// </summary>
public enum ServantState : byte
{
    /// <summary>The coffin holds a revivable or reviving servant.</summary>
    Dead = (byte)Layout.ServantInfoStateDead,

    /// <summary>Back from a hunt with an injury that has not healed yet.</summary>
    Injured = (byte)Layout.ServantInfoStateInjured,

    /// <summary>On a hunt for this castle.</summary>
    AwayOnHunt = (byte)Layout.ServantInfoStateAwayOnHunt,

    /// <summary>Home, but carrying items; the game sends no servant with items.</summary>
    HasItems = (byte)Layout.ServantInfoStateHasItemInInventory,

    /// <summary>Home with an empty inventory: can be sent.</summary>
    Free = (byte)Layout.ServantInfoStateFree,
}

/// <summary>How one servant came back from a hunt.</summary>
public enum ServantOutcome
{
    /// <summary>Back and unhurt.</summary>
    Returned = 0,

    /// <summary>Back, injured (cannot be sent until the injury heals).</summary>
    Injured = 1,

    /// <summary>Did not come back (the game destroyed the servant; the coffin can revive it).</summary>
    Dead = 2,
}

/// <summary>
/// A map zone as the game names it in a hunt (<c>MapZoneId</c>): the terrain chunk and the zone
/// byte. <see cref="ZoneId"/> is the zone's index in its chunk plus one, the way the game's send
/// compares it.
/// </summary>
public readonly struct MissionZone : IEquatable<MissionZone>
{
    /// <summary>Terrain chunk X.</summary>
    public sbyte ChunkX { get; }

    /// <summary>Terrain chunk Y.</summary>
    public sbyte ChunkY { get; }

    /// <summary>Zone byte: the zone's index in its chunk plus one. 0 means no zone.</summary>
    public byte ZoneId { get; }

    /// <summary>A zone from its three bytes.</summary>
    public MissionZone(sbyte chunkX, sbyte chunkY, byte zoneId)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
        ZoneId = zoneId;
    }

    /// <summary>The empty zone.</summary>
    public static MissionZone None => default;

    /// <summary>True for the empty zone.</summary>
    public bool IsNone => ZoneId == 0 && ChunkX == 0 && ChunkY == 0;

    /// <inheritdoc />
    public bool Equals(MissionZone other) => ChunkX == other.ChunkX && ChunkY == other.ChunkY && ZoneId == other.ZoneId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MissionZone other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (ChunkX & 0xFF) | ((ChunkY & 0xFF) << 8) | (ZoneId << 16);

    /// <summary><c>x,y#zone</c>.</summary>
    public override string ToString() =>
        ChunkX.ToString(CultureInfo.InvariantCulture) + "," + ChunkY.ToString(CultureInfo.InvariantCulture)
        + "#" + ZoneId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Value equality.</summary>
    public static bool operator ==(MissionZone left, MissionZone right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(MissionZone left, MissionZone right) => !left.Equals(right);
}

/// <summary>
/// One servant of a castle, read the way the game builds the throne's list (every coffin connected
/// to the castle's heart that holds a living, revivable or reviving servant).
/// </summary>
public sealed class ServantInfo
{
    /// <summary>The servant unit. Session id: valid in this world load only.</summary>
    public CastleId Servant { get; init; }

    /// <summary>The servant's coffin.</summary>
    public CastleId Coffin { get; init; }

    /// <summary>The castle heart the coffin is connected to.</summary>
    public CastleId Heart { get; init; }

    /// <summary>The coffin's servant name (the name the player gave it).</summary>
    public string Name { get; init; } = "";

    /// <summary>The state the throne shows.</summary>
    public ServantState State { get; init; }

    /// <summary>When the current injury heals (UTC); null when not injured.</summary>
    public DateTime? InjuryEndsUtc { get; init; }

    /// <summary>The injury's prefab GUID hash, 0 when none.</summary>
    public int Injury { get; init; }

    /// <summary>Hunt proficiency as the throne shows it (from gear level and expertise).</summary>
    public float HuntProficiency { get; init; }

    /// <summary>The loot factor the throne shows (expertise times one half).</summary>
    public float LootFactor { get; init; }

    /// <summary><c>ServantPower.GearLevel</c>.</summary>
    public float GearLevel { get; init; }

    /// <summary><c>ServantPower.Expertise</c>.</summary>
    public float Expertise { get; init; }

    /// <summary>First perk GUID hash, 0 when none.</summary>
    public int Perk0 { get; init; }

    /// <summary>Second perk GUID hash, 0 when none.</summary>
    public int Perk1 { get; init; }

    /// <summary>The servant's inventory (empty reference when the servant is dead).</summary>
    public InventoryRef Inventory { get; init; }

    /// <summary>Stacks in the servant's inventory (-1 when unreadable).</summary>
    public int ItemStacks { get; init; }

    /// <summary>The world-load epoch the ids were read in; ids from another epoch are refused.</summary>
    public int Epoch { get; init; }

    /// <summary>One line: <c>name (state) servant=i:v coffin=i:v</c>.</summary>
    public override string ToString() => Name + " (" + State + ") servant=" + Servant + " coffin=" + Coffin;
}

/// <summary>One hunt in a castle heart's mission record (<c>ActiveServantMission</c>).</summary>
public sealed class ServantMission
{
    /// <summary>The castle heart that owns the record.</summary>
    public CastleId Heart { get; init; }

    /// <summary>Row in the heart's record (changes as hunts start and end).</summary>
    public int Row { get; init; }

    /// <summary>The zone's mission prefab GUID hash (<c>MapZoneData.ServantMissionAsset</c>).</summary>
    public int MissionGuid { get; init; }

    /// <summary>The mission prefab's name, or "" when unknown.</summary>
    public string MissionName { get; init; } = "";

    /// <summary>The map zone whose mission this is; <see cref="MissionZone.None"/> when no zone names it.</summary>
    public MissionZone Zone { get; init; }

    /// <summary>The hunt length choice: an index into the game's mission settings.</summary>
    public int DataSlot { get; init; }

    /// <summary>When the hunt left (UTC).</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>The hunt's length in seconds.</summary>
    public float LengthSeconds { get; init; }

    /// <summary>When the game finishes the hunt (UTC).</summary>
    public DateTime EndsUtc { get; init; }

    /// <summary>The servants on the hunt (1 to 3).</summary>
    public IReadOnlyList<CastleId> Servants { get; init; } = Array.Empty<CastleId>();

    /// <summary>The world-load epoch the ids were read in.</summary>
    public int Epoch { get; init; }
}

/// <summary>An item that came back with a servant.</summary>
public readonly struct ServantLoot
{
    /// <summary>Item prefab GUID hash.</summary>
    public int Item { get; }

    /// <summary>How many.</summary>
    public int Amount { get; }

    /// <summary>A loot row.</summary>
    public ServantLoot(int item, int amount)
    {
        Item = item;
        Amount = amount;
    }

    /// <summary><c>amount x item</c>.</summary>
    public override string ToString() => Amount.ToString(CultureInfo.InvariantCulture) + " x " + Item.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One servant of a finished hunt.</summary>
public sealed class ReturnedServant
{
    /// <summary>The servant unit (destroyed when <see cref="Outcome"/> is <see cref="ServantOutcome.Dead"/>).</summary>
    public CastleId Servant { get; init; }

    /// <summary>The servant's coffin; none when it could not be found.</summary>
    public CastleId Coffin { get; init; }

    /// <summary>The coffin's servant name, or "" when unknown.</summary>
    public string Name { get; init; } = "";

    /// <summary>Back, injured or dead.</summary>
    public ServantOutcome Outcome { get; init; }

    /// <summary>When the injury heals (UTC), when injured.</summary>
    public DateTime? InjuryEndsUtc { get; init; }

    /// <summary>The servant's inventory; empty reference when dead.</summary>
    public InventoryRef Inventory { get; init; }

    /// <summary>
    /// What is in the servant's inventory when the return is published, item by item. The game
    /// sends only servants with an empty inventory, so this is the hunt's loot unless something
    /// wrote to the servant's inventory during the hunt or in the moment before the publish.
    /// </summary>
    public IReadOnlyList<ServantLoot> Loot { get; init; } = Array.Empty<ServantLoot>();

    /// <summary>
    /// True when the servant came back and its inventory was read (<see cref="Loot"/> holds what it
    /// carries); false when it died or the inventory could not be read.
    /// </summary>
    public bool LootIsHaul { get; init; }
}

/// <summary>
/// Published on the server <see cref="Progenitor.Events.EventBus"/> a moment after the game finished
/// a hunt (half a second to five seconds): the servants are back in the castle, the loot is in their
/// inventories, the injuries are applied, the row is gone from the heart's mission record, the
/// servants that died are destroyed and the survivors' mission buff is gone, so a handler can call
/// <c>Resend</c>. <see cref="ReturnedUtc"/> is when the return was seen. Subscribe with
/// <c>ctx.Events.On&lt;ServantsReturned&gt;</c>; Progenitor watches for returns only while something
/// is subscribed. Experimental: see <c>docs/modules.md</c>, Servants.
/// </summary>
public sealed class ServantsReturned
{
    /// <summary>The castle heart that sent the hunt.</summary>
    public CastleId Heart { get; init; }

    /// <summary>The heart's stable id (<see cref="ContainerId.None"/> before the id book loads).</summary>
    public ContainerId HeartId { get; init; }

    /// <summary>The castle's territory index (-1 when unknown): the durable key for the castle.</summary>
    public int Territory { get; init; } = -1;

    /// <summary>The zone's mission prefab GUID hash.</summary>
    public int MissionGuid { get; init; }

    /// <summary>The mission prefab's name, or "".</summary>
    public string MissionName { get; init; } = "";

    /// <summary>The hunt's map zone; <see cref="MissionZone.None"/> when no zone names the mission.</summary>
    public MissionZone Zone { get; init; }

    /// <summary>The hunt length choice (index into the game's mission settings).</summary>
    public int DataSlot { get; init; }

    /// <summary>The hunt's length in seconds.</summary>
    public float LengthSeconds { get; init; }

    /// <summary>When the hunt left (UTC).</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>When Progenitor saw it finish (UTC).</summary>
    public DateTime ReturnedUtc { get; init; }

    /// <summary>The servants that went, in the record's order.</summary>
    public IReadOnlyList<ReturnedServant> Servants { get; init; } = Array.Empty<ReturnedServant>();

    /// <summary>The world-load epoch the ids belong to; <c>Resend</c> refuses a return from another epoch.</summary>
    public int Epoch { get; init; }

    /// <summary>Every servant's loot added up.</summary>
    public IReadOnlyList<ServantLoot> TotalLoot => ServantLogic.SumLoot(Servants);
}

/// <summary>
/// Published on the server <see cref="Progenitor.Events.EventBus"/> after a send Progenitor made
/// (<c>ctx.Servants.Send</c>, <c>Resend</c>, or a throne send routed for a swapped list) wrote the
/// heart's mission record. Sends the game makes itself are not announced; read them with
/// <c>ctx.Servants.Missions</c>.
/// </summary>
public sealed class ServantsSent
{
    /// <summary>The result of the send (always <see cref="ServantSendResult.Ok"/>).</summary>
    public ServantSendResult Result { get; init; } = ServantSendResult.Refused(ServantSendRefusal.None, "");

    /// <summary>Who asked: the plugin GUID, or <c>progenitor.throne</c> for a routed throne send.</summary>
    public string By { get; init; } = "";
}

/// <summary>Why a send was refused. <see cref="None"/> on success.</summary>
public enum ServantSendRefusal
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>The castle heart is gone, stale, or has no mission record.</summary>
    HeartGone,

    /// <summary>No servant named, or more than three.</summary>
    ServantCount,

    /// <summary>The same servant is named twice.</summary>
    DuplicateServant,

    /// <summary>A named servant does not exist, is from an earlier world load, or is not a servant.</summary>
    ServantGone,

    /// <summary>A named servant is dead.</summary>
    ServantDead,

    /// <summary>A named servant is injured.</summary>
    ServantInjured,

    /// <summary>A named servant is already on a hunt.</summary>
    ServantBusy,

    /// <summary>A named servant still carries items (the game sends only empty servants).</summary>
    ServantHasItems,

    /// <summary>A named servant's coffin is connected to another castle heart.</summary>
    ServantOfOtherCastle,

    /// <summary>The heart already has a hunt on this zone (the game allows one per zone and castle).</summary>
    ZoneBusy,

    /// <summary>No map zone or mission prefab matches the mission.</summary>
    MissionUnknown,

    /// <summary>The user has not discovered the zone (the game's progression check).</summary>
    ZoneLocked,

    /// <summary>The user has no readable progression (the game's send needs it).</summary>
    NoProgression,

    /// <summary>More servants than the mission's slots.</summary>
    TooManyForMission,

    /// <summary>The hunt length choice is not one of the game's mission settings.</summary>
    DataSlotUnknown,

    /// <summary>The game's mission settings could not be read.</summary>
    SettingsMissing,

    /// <summary>The write itself failed; nothing or part was written (the reason says which).</summary>
    WriteFailed,
}

/// <summary>A send request for <c>ctx.Servants.Send</c>.</summary>
public sealed class ServantSendRequest
{
    /// <summary>The castle heart whose mission record gets the hunt (the servants' own heart).</summary>
    public CastleId Heart { get; init; }

    /// <summary>One to three servants, all of that heart.</summary>
    public IReadOnlyList<CastleId> Servants { get; init; } = Array.Empty<CastleId>();

    /// <summary>The zone's mission prefab GUID hash (<see cref="ServantMission.MissionGuid"/>).</summary>
    public int MissionGuid { get; init; }

    /// <summary>The hunt length choice (<see cref="ServantMission.DataSlot"/>).</summary>
    public int DataSlot { get; init; }

    /// <summary>
    /// The user whose map discovery is checked, as the game checks the sender's. None means the
    /// heart's owner.
    /// </summary>
    public CastleId User { get; init; }

    /// <summary>
    /// Check that <see cref="User"/> discovered the zone (default true, like the game). The
    /// server's "all progression unlocked" debug setting turns the check off, as in the game.
    /// </summary>
    public bool CheckDiscovered { get; init; } = true;

    /// <summary>
    /// The world-load epoch the ids were read in (<c>ctx.WorldState.LoadEpoch</c>); a request from
    /// another epoch is refused. 0 means the current epoch.
    /// </summary>
    public int Epoch { get; init; }
}

/// <summary>What a send did.</summary>
public sealed class ServantSendResult
{
    /// <summary>True when the heart's mission record got the hunt and every servant got the mission buff.</summary>
    public bool Ok { get; init; }

    /// <summary>Why not, or <see cref="ServantSendRefusal.None"/>.</summary>
    public ServantSendRefusal Refusal { get; init; }

    /// <summary>A sentence naming the reason (the servant, the zone, the setting); "" on success.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The castle heart.</summary>
    public CastleId Heart { get; init; }

    /// <summary>The zone's mission prefab GUID hash.</summary>
    public int MissionGuid { get; init; }

    /// <summary>The map zone, when known.</summary>
    public MissionZone Zone { get; init; }

    /// <summary>The hunt length choice.</summary>
    public int DataSlot { get; init; }

    /// <summary>The hunt's length in seconds as written.</summary>
    public float LengthSeconds { get; init; }

    /// <summary>When the hunt left (UTC).</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>The servants written into the record.</summary>
    public IReadOnlyList<CastleId> Servants { get; init; } = Array.Empty<CastleId>();

    /// <summary>
    /// True when every servant carried the mission buff right after the send (the buff's script
    /// marks the servant as on a hunt). One second later Progenitor checks again that each servant
    /// carries the buff and reads as on a hunt, and logs <c>servants: send confirmed</c> or a
    /// warning naming the servant.
    /// </summary>
    public bool BuffApplied { get; init; }

    internal static ServantSendResult Refused(ServantSendRefusal refusal, string reason) =>
        new() { Ok = false, Refusal = refusal, Reason = reason };

    /// <summary><c>sent ...</c> or <c>refused (Refusal): reason</c>.</summary>
    public override string ToString() =>
        Ok
            ? "sent " + Servants.Count + " servant(s) on mission " + MissionGuid + " slot " + DataSlot + " for "
              + LengthSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s"
            : "refused (" + Refusal + "): " + Reason;
}

/// <summary>What a <see cref="IServantListProvider"/> is asked when a player opens a castle throne.</summary>
public sealed class ServantListRequest
{
    /// <summary>The player's user entity.</summary>
    public CastleId User { get; init; }

    /// <summary>The player's character.</summary>
    public CastleId Character { get; init; }

    /// <summary>The castle heart of the castle the player stands in (the throne's castle).</summary>
    public CastleId Heart { get; init; }

    /// <summary>The castle's territory index, -1 when the player stands on none.</summary>
    public int Territory { get; init; } = -1;
}

/// <summary>
/// Chooses, per player, whose servants a castle throne lists (<c>ctx.Servants.RegisterListProvider</c>).
/// Experimental. Called on the server main thread each time a player opens a throne; keep it cheap.
/// </summary>
public interface IServantListProvider
{
    /// <summary>
    /// The castle hearts whose servants the throne lists, in order: each heart's servants are listed
    /// the way the game lists a throne's own (every coffin of that heart holding a living, revivable
    /// or reviving servant), up to 36 servants in all. Return null to leave the game's own list. A
    /// send from the list that names servants of another heart is performed by Progenitor on their
    /// own heart, with the game's checks.
    /// </summary>
    IReadOnlyList<CastleId>? HeartsFor(ServantListRequest request);
}

/// <summary>What the throne list hook can do in this process right now.</summary>
public sealed class ServantListStatus
{
    /// <summary>True when a provider is registered.</summary>
    public bool Registered { get; init; }

    /// <summary>The registered provider's owner (plugin GUID), or "".</summary>
    public string Owner { get; init; } = "";

    /// <summary>Lists swapped since the provider registered.</summary>
    public long Swaps { get; init; }

    /// <summary>Throne sends Progenitor performed for a swapped list.</summary>
    public long RoutedSends { get; init; }

    /// <summary>Throne sends for a swapped list that Progenitor refused (the client event was dropped).</summary>
    public long RoutedRefusals { get; init; }

    /// <summary>One line for logs.</summary>
    public override string ToString() =>
        "servant list: provider=" + (Registered ? Owner : "none") + " swaps=" + Swaps
        + " routed=" + RoutedSends + " refused=" + RoutedRefusals;
}
