using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Castles;
using Progenitor.Inventory;

namespace Progenitor.Servants;

/// <summary>One servant as the send validation sees it (read on the server, checked here).</summary>
internal sealed class ServantFacts
{
    public CastleId Servant;
    public bool Exists;
    public bool IsServant;
    public bool OnMission;
    public bool Dead;
    public int ItemStacks = -1;
    public CastleId Coffin;
    public CastleId CoffinHeart;
    public long InjuryEndTicks;
    public string Name = "";
}

/// <summary>One servant of a finished hunt as the return publish sees it (read on the server, judged here).</summary>
internal sealed class ReturnFacts
{
    /// <summary>The servant entity named in the hunt row still exists.</summary>
    public bool Exists;
    /// <summary>It carries the game's <c>Dead</c> component.</summary>
    public bool HasDead;
    /// <summary>It carries <c>DestroyTag</c> (destroyed, removal pending).</summary>
    public bool HasDestroyTag;
    /// <summary>It carries <c>DestroyData</c>: every live unit does; never evidence of a death.</summary>
    public bool HasDestroyData;
    /// <summary><c>ServantData.IsOnMission</c>.</summary>
    public bool OnMission;
    /// <summary>The hunt's mission buff is still on it.</summary>
    public bool MissionBuffOn;
    /// <summary>A current row of the heart names it again (it was sent out after this return).</summary>
    public bool OutAgain;
    /// <summary>Its coffin was found and read.</summary>
    public bool CoffinReadable;
    /// <summary>The coffin's <c>State</c> (7 alive, 8 revivable, 9 reviving).</summary>
    public int CoffinState;
    /// <summary>The coffin's <c>InjuryEndTimeTicks</c>.</summary>
    public long InjuryEndTicks;
    /// <summary>The coffin names a living servant other than the row's entity (the servant was re-linked).</summary>
    public bool CoffinServantAlive;
}

/// <summary>One <c>ActiveServantMission</c> row, decoded or about to be encoded.</summary>
internal sealed class MissionRow
{
    public int MissionGuid;
    public long StartTicks;
    public float LengthSeconds;
    public int DataSlot;
    public CastleId Servant1;
    public CastleId Servant2;
    public CastleId Servant3;
    public int Count;

    public IEnumerable<CastleId> Servants()
    {
        if (!Servant1.IsNone) yield return Servant1;
        if (!Servant2.IsNone) yield return Servant2;
        if (!Servant3.IsNone) yield return Servant3;
    }

    public bool Names(CastleId servant) =>
        !servant.IsNone && (Servant1 == servant || Servant2 == servant || Servant3 == servant);

    /// <summary>Identity of a row across a frame: mission, start and first servant.</summary>
    public (int, long, CastleId) Key => (MissionGuid, StartTicks, Servant1);
}

/// <summary>Everything the send validation needs, read on the server before any write.</summary>
internal sealed class SendFacts
{
    public CastleId Heart;
    public bool HeartOk;
    public string HeartReason = "";
    public IReadOnlyList<ServantFacts> Servants = Array.Empty<ServantFacts>();
    public IReadOnlyList<MissionRow> Existing = Array.Empty<MissionRow>();
    public int MissionGuid;
    public string MissionName = "";
    public bool ZoneKnown;
    public MissionZone Zone;
    public bool MissionDataKnown;
    public int ServantSlots;
    public int DataSlot;
    public bool SettingsReadable;
    public int SettingCount;
    public float SettingLength;
    public bool CheckDiscovered;
    public bool SkipProgression;
    public bool ProgressionReadable;
    public bool Discovered;
    public bool TimersDisabled;
    public long NowTicks;
}

/// <summary>A planned send: the row to append, or the refusal.</summary>
internal sealed class SendPlan
{
    public bool Ok;
    public ServantSendRefusal Refusal;
    public string Reason = "";
    public MissionRow? Row;
}

/// <summary>
/// The game-free half of <c>ctx.Servants</c>: the throne's state rule, the proficiency shown in the
/// throne list, hunt timing, the send validation (the checks the game's own send makes, in its
/// order) and the byte layouts of a mission row and a throne list entry. The server half reads
/// the facts and writes the bytes these functions produce.
/// </summary>
internal static class ServantLogic
{
    /// <summary>Most servants one hunt takes (the game's send event has three slots).</summary>
    internal const int MaxServantsPerHunt = 3;

    /// <summary>Ticks per second (<see cref="DateTime"/> ticks).</summary>
    internal const long TicksPerSecond = 10_000_000L;

    /// <summary>The factor the game applies to a hunt's length when its servant-timers debug switch is on.</summary>
    internal const float TimersDisabledFactor = 1e-5f;

    /// <summary>Byte size of the throne list (<c>FixedList4096Bytes</c>).</summary>
    internal const int ResponseListBytes = 4096;

    /// <summary>Gear level weight in the throne's hunt proficiency (<c>ServantPower.GetHuntProficiency</c>).</summary>
    internal const float ProficiencyGearFactor = 10f;

    /// <summary>Expertise weight in the throne's hunt proficiency.</summary>
    internal const float ProficiencyExpertiseFactor = 100f;

    /// <summary>The proficiency every servant starts from.</summary>
    internal const int ProficiencyBase = 100;

    /// <summary>Expertise weight of the loot factor the throne shows (<c>ServantPower.GetLootFactor(raw: true)</c>).</summary>
    internal const float LootFactorExpertise = 0.5f;

    // ---- the throne list ------------------------------------------------------------------------

    /// <summary>
    /// The state the throne shows for a servant, in the game's order: a revivable or reviving
    /// coffin is dead; an injury that has not healed; on a hunt of this castle; carrying items;
    /// otherwise free.
    /// </summary>
    internal static ServantState StateOf(int coffinState, long injuryEndTicks, long nowTicks, bool onHuntOfThisHeart, bool inventoryEmpty)
    {
        if (coffinState == Layout.ServantCoffinStateServantRevivable || coffinState == Layout.ServantCoffinStateReviving)
            return ServantState.Dead;
        if (injuryEndTicks > nowTicks) return ServantState.Injured;
        if (onHuntOfThisHeart) return ServantState.AwayOnHunt;
        return inventoryEmpty ? ServantState.Free : ServantState.HasItems;
    }

    /// <summary>True for a coffin the throne lists (a living, revivable or reviving servant).</summary>
    internal static bool ThroneLists(int coffinState) => coffinState >= Layout.ServantCoffinStateServantAlive;

    /// <summary>
    /// Hunt proficiency as the throne shows it: gear level times 10 and expertise times 100, each
    /// rounded to the nearest integer (ties to even), plus 100.
    /// </summary>
    internal static float HuntProficiency(float gearLevel, float expertise)
    {
        float gear = gearLevel * ProficiencyGearFactor;
        float skill = expertise * ProficiencyExpertiseFactor;
        int sum = (int)Math.Round((double)gear) + ProficiencyBase + (int)Math.Round((double)skill);
        return sum;
    }

    /// <summary>The loot factor the throne shows: expertise times one half.</summary>
    internal static float LootFactor(float expertise) => expertise * LootFactorExpertise;

    /// <summary>
    /// The servants a provider asked for, deduplicated and capped at what the throne list holds.
    /// Order is kept.
    /// </summary>
    internal static List<CastleId> CapList(IReadOnlyList<CastleId>? asked, int capacity)
    {
        var list = new List<CastleId>();
        if (asked == null) return list;
        var seen = new HashSet<CastleId>();
        foreach (var id in asked)
        {
            if (id.IsNone || !seen.Add(id)) continue;
            if (list.Count >= capacity) break;
            list.Add(id);
        }
        return list;
    }

    /// <summary>
    /// Re-authorization of a routed throne send: every heart the send names must still be among the
    /// hearts the provider returns for that player now. Null when allowed, else the reason.
    /// </summary>
    internal static string? RouteRefusal(IReadOnlyList<CastleId> targets, IReadOnlyList<CastleId>? allowedNow)
    {
        if (allowedNow == null)
            return "the list provider no longer swaps this player's list";
        var allowed = new HashSet<CastleId>(allowedNow);
        foreach (var heart in targets)
            if (!allowed.Contains(heart))
                return "the list provider no longer lists castle heart " + heart + " for this player";
        return null;
    }

    // ---- hunt timing ------------------------------------------------------------------------------

    /// <summary>When the game finishes a hunt: start plus length, in ticks (<c>GetCompletionTicks</c>).</summary>
    internal static long CompletionTicks(long startTicks, float lengthSeconds) =>
        (long)((double)lengthSeconds * 1e7) + startTicks;

    /// <summary>
    /// True when the game may finish this hunt on its next update: due now or within
    /// <paramref name="slackTicks"/> (the game reads the clock after Progenitor does), or at once
    /// when servant timers are off.
    /// </summary>
    internal static bool IsDue(MissionRow row, long nowTicks, long slackTicks, bool timersDisabled) =>
        timersDisabled || CompletionTicks(row.StartTicks, row.LengthSeconds) <= nowTicks + slackTicks;

    /// <summary>The length the game writes for a hunt: the setting's length, times 1e-5 with timers off.</summary>
    internal static float HuntLength(float settingLength, bool timersDisabled) =>
        settingLength * (timersDisabled ? TimersDisabledFactor : 1f);

    /// <summary>Ticks to a UTC date; ticks outside the range give <see cref="DateTime.MinValue"/>.</summary>
    internal static DateTime Utc(long ticks) =>
        ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);

    /// <summary>
    /// The rows of <paramref name="before"/> that are gone from <paramref name="after"/>: the hunts
    /// the game finished. A row is matched by mission, start ticks and first servant.
    /// </summary>
    internal static List<MissionRow> Finished(IReadOnlyList<MissionRow> before, IReadOnlyList<MissionRow> after)
    {
        var keys = new HashSet<(int, long, CastleId)>();
        foreach (var r in after) keys.Add(r.Key);
        var gone = new List<MissionRow>();
        foreach (var r in before)
            if (!keys.Contains(r.Key)) gone.Add(r);
        return gone;
    }

    /// <summary>
    /// True when the servant is positively being destroyed: the game's <c>Dead</c> component or
    /// <c>DestroyTag</c>. <paramref name="hasDestroyData"/> is ignored on purpose: <c>DestroyData</c>
    /// and <c>DestroyState</c> are ordinary components every live unit carries (the destroy
    /// reason slot), so they are no evidence of a destroy.
    /// </summary>
    internal static bool Destroying(bool hasDead, bool hasDestroyTag, bool hasDestroyData = false) => hasDead || hasDestroyTag;

    /// <summary>
    /// True when the game is done with this servant's return, so the outcome and loot can be read:
    /// it is gone or being destroyed, it was sent out again (a send needs the old mission buff gone),
    /// or it is no longer marked on a hunt and carries no mission buff.
    /// </summary>
    internal static bool ReturnSettled(ReturnFacts f)
    {
        if (f.OutAgain) return true;
        if (!f.Exists || Destroying(f.HasDead, f.HasDestroyTag, f.HasDestroyData)) return true;
        return !f.OnMission && !f.MissionBuffOn;
    }

    /// <summary>
    /// A servant's outcome after a return, Dead only on positive evidence: its entity is gone or
    /// being destroyed (and its coffin names no other living servant), or its coffin is in the
    /// game's dead state (revivable or reviving). An injury running is injured; otherwise returned.
    /// </summary>
    internal static ServantOutcome OutcomeOf(ReturnFacts f, long nowTicks)
    {
        bool coffinDead = f.CoffinReadable
            && (f.CoffinState == Layout.ServantCoffinStateServantRevivable || f.CoffinState == Layout.ServantCoffinStateReviving);
        if (coffinDead) return ServantOutcome.Dead;
        bool alive = f.Exists && !Destroying(f.HasDead, f.HasDestroyTag, f.HasDestroyData);
        if (!alive && !f.CoffinServantAlive) return ServantOutcome.Dead;
        long injury = f.CoffinReadable ? f.InjuryEndTicks : 0;
        return injury > nowTicks ? ServantOutcome.Injured : ServantOutcome.Returned;
    }

    /// <summary>
    /// A servant's main inventory, the one reference both <c>ServantInfo.Inventory</c> and
    /// <c>ReturnedServant.Inventory</c> use: the servant entity's instance 0 in <paramref name="epoch"/>.
    /// </summary>
    internal static InventoryRef ServantInventory(CastleId servant, int epoch) =>
        servant.IsNone ? InventoryRef.None : new InventoryRef(servant.Index, servant.Version, 0, epoch);

    /// <summary>
    /// One servant of a return as the event carries it. <paramref name="resolved"/> is the living
    /// servant found at the publish (the row's entity, or the one its coffin names now); a servant
    /// that is not dead carries that servant's id and inventory, so both are valid for
    /// <c>ctx.Inventory</c> in a handler (the same reference <c>ctx.Servants.List</c> gives), and a
    /// dead one keeps the row's id with no inventory. <paramref name="stacks"/> is -1 when the
    /// inventory could not be read.
    /// </summary>
    internal static ReturnedServant Returned(CastleId rowServant, CastleId resolved, CastleId coffin, string name, ReturnFacts f,
        long nowTicks, int epoch, IReadOnlyList<ServantLoot> loot, int stacks)
    {
        var outcome = OutcomeOf(f, nowTicks);
        bool dead = outcome == ServantOutcome.Dead;
        var who = dead || resolved.IsNone ? rowServant : resolved;
        return new ReturnedServant
        {
            Servant = who,
            Coffin = coffin,
            Name = name,
            Outcome = outcome,
            InjuryEndsUtc = outcome == ServantOutcome.Injured ? Utc(f.InjuryEndTicks) : null,
            Inventory = dead ? InventoryRef.None : ServantInventory(who, epoch),
            // Sent out again before this publish: its loot was moved for that send, so what it
            // carries now is not this hunt's haul.
            Loot = dead || f.OutAgain ? Array.Empty<ServantLoot>() : loot,
            LootIsHaul = !dead && stacks >= 0 && !f.OutAgain,
        };
    }

    /// <summary>Add up inventory slots (item, amount) into loot rows, in first-seen order.</summary>
    internal static List<ServantLoot> Loot(IEnumerable<(int Item, int Amount)> slots)
    {
        var order = new List<int>();
        var sums = new Dictionary<int, long>();
        foreach (var (item, amount) in slots)
        {
            if (item == 0 || amount <= 0) continue;
            if (!sums.TryGetValue(item, out long n)) order.Add(item);
            sums[item] = n + amount;
        }
        var list = new List<ServantLoot>(order.Count);
        foreach (int item in order) list.Add(new ServantLoot(item, (int)Math.Min(int.MaxValue, sums[item])));
        return list;
    }

    /// <summary>Every servant's loot added up, in first-seen order.</summary>
    internal static IReadOnlyList<ServantLoot> SumLoot(IReadOnlyList<ReturnedServant> servants)
    {
        var rows = new List<(int, int)>();
        foreach (var s in servants)
            foreach (var l in s.Loot) rows.Add((l.Item, l.Amount));
        return Loot(rows);
    }

    /// <summary>
    /// The servants of a return that can go again: back unhurt (dead and injured servants are left
    /// home). Their inventories are not checked here; <see cref="PlanSend"/> refuses a servant that
    /// still carries loot.
    /// </summary>
    internal static List<CastleId> Survivors(IReadOnlyList<ReturnedServant> servants)
    {
        var list = new List<CastleId>();
        foreach (var s in servants)
            if (s.Outcome == ServantOutcome.Returned && !s.Servant.IsNone) list.Add(s.Servant);
        return list;
    }

    // ---- the send ---------------------------------------------------------------------------------

    /// <summary>
    /// Validate a send with the game's own checks, in the game's order, and plan the row the game
    /// would add to the heart's mission record. Nothing is written here. Refusals name the servant,
    /// zone or setting.
    /// </summary>
    internal static SendPlan PlanSend(SendFacts f)
    {
        if (!f.HeartOk)
            return Refuse(ServantSendRefusal.HeartGone, f.HeartReason.Length > 0 ? f.HeartReason : "the castle heart " + f.Heart + " is gone or has no mission record");

        if (f.CheckDiscovered && !f.SkipProgression)
        {
            if (!f.ProgressionReadable)
                return Refuse(ServantSendRefusal.NoProgression, "the user's map progression could not be read (the game's send needs it)");
        }
        if (!f.ZoneKnown || f.MissionGuid == 0)
            return Refuse(ServantSendRefusal.MissionUnknown, "no map zone has mission " + f.MissionGuid);
        if (f.CheckDiscovered && !f.SkipProgression && !f.Discovered)
            return Refuse(ServantSendRefusal.ZoneLocked, "the zone " + f.Zone + " (mission " + Label(f) + ") is not discovered by the user");

        if (f.Servants.Count == 0 || f.Servants.Count > MaxServantsPerHunt)
            return Refuse(ServantSendRefusal.ServantCount, "a hunt takes 1 to " + MaxServantsPerHunt + " servants; " + f.Servants.Count + " named");

        var seen = new HashSet<CastleId>();
        foreach (var s in f.Servants)
            if (!seen.Add(s.Servant))
                return Refuse(ServantSendRefusal.DuplicateServant, "servant " + Who(s) + " is named twice");

        foreach (var s in f.Servants)
        {
            if (!s.Exists || !s.IsServant)
                return Refuse(ServantSendRefusal.ServantGone, "servant " + Who(s) + " does not exist or is not a servant");
            if (s.OnMission)
                return Refuse(ServantSendRefusal.ServantBusy, "servant " + Who(s) + " is already on a hunt");
            if (s.Dead)
                return Refuse(ServantSendRefusal.ServantDead, "servant " + Who(s) + " is dead");
            if (s.ItemStacks != 0)
                return Refuse(ServantSendRefusal.ServantHasItems, s.ItemStacks < 0
                    ? "servant " + Who(s) + "'s inventory could not be read"
                    : "servant " + Who(s) + " still carries " + s.ItemStacks + " stack(s); the game sends only servants with an empty inventory");
            if (s.Coffin.IsNone)
                return Refuse(ServantSendRefusal.ServantGone, "servant " + Who(s) + " has no coffin");
            if (s.InjuryEndTicks > f.NowTicks)
                return Refuse(ServantSendRefusal.ServantInjured, "servant " + Who(s) + " is injured until "
                    + Utc(s.InjuryEndTicks).ToString("u", CultureInfo.InvariantCulture));
        }

        foreach (var s in f.Servants)
            if (s.CoffinHeart != f.Heart)
                return Refuse(ServantSendRefusal.ServantOfOtherCastle, "servant " + Who(s) + "'s coffin belongs to castle heart "
                    + s.CoffinHeart + ", not " + f.Heart);

        foreach (var row in f.Existing)
        {
            if (row.MissionGuid == f.MissionGuid)
                return Refuse(ServantSendRefusal.ZoneBusy, "this castle already has a hunt on mission " + Label(f)
                    + " (one hunt per zone and castle)");
            foreach (var s in f.Servants)
                if (row.Names(s.Servant))
                    return Refuse(ServantSendRefusal.ServantBusy, "servant " + Who(s) + " is in this castle's hunt on mission " + row.MissionGuid);
        }

        if (!f.MissionDataKnown)
            return Refuse(ServantSendRefusal.MissionUnknown, "mission " + Label(f) + " has no MissionData prefab");
        if (f.Servants.Count > f.ServantSlots)
            return Refuse(ServantSendRefusal.TooManyForMission, "mission " + Label(f) + " takes " + f.ServantSlots + " servant(s); "
                + f.Servants.Count + " named");

        if (!f.SettingsReadable)
            return Refuse(ServantSendRefusal.SettingsMissing, "the game's servant mission settings could not be read");
        if (f.DataSlot < 0 || f.DataSlot >= f.SettingCount)
            return Refuse(ServantSendRefusal.DataSlotUnknown, "hunt length choice " + f.DataSlot + " is not one of the game's "
                + f.SettingCount + " mission settings");

        var row2 = new MissionRow
        {
            MissionGuid = f.MissionGuid,
            StartTicks = f.NowTicks,
            LengthSeconds = HuntLength(f.SettingLength, f.TimersDisabled),
            DataSlot = f.DataSlot,
            Servant1 = f.Servants.Count > 0 ? f.Servants[0].Servant : CastleId.None,
            Servant2 = f.Servants.Count > 1 ? f.Servants[1].Servant : CastleId.None,
            Servant3 = f.Servants.Count > 2 ? f.Servants[2].Servant : CastleId.None,
            Count = f.Servants.Count,
        };
        return new SendPlan { Ok = true, Row = row2 };
    }

    private static SendPlan Refuse(ServantSendRefusal refusal, string reason) =>
        new() { Ok = false, Refusal = refusal, Reason = reason };

    private static string Who(ServantFacts s) => s.Name.Length > 0 ? "'" + s.Name + "' (" + s.Servant + ")" : s.Servant.ToString();

    private static string Label(SendFacts f) => f.MissionName.Length > 0 ? f.MissionName + " (" + f.MissionGuid + ")" : f.MissionGuid.ToString(CultureInfo.InvariantCulture);

    // ---- byte layouts -----------------------------------------------------------------------------

    /// <summary>Encode a mission row into <paramref name="dest"/> (<see cref="Layout.ActiveServantMissionStride"/> bytes).</summary>
    internal static void EncodeRow(MissionRow row, Span<byte> dest)
    {
        if (dest.Length < Layout.ActiveServantMissionStride)
            throw new ArgumentException("a mission row needs " + Layout.ActiveServantMissionStride + " bytes", nameof(dest));
        dest.Slice(0, Layout.ActiveServantMissionStride).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.ActiveServantMissionMissionIdOffset), row.MissionGuid);
        BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(Layout.ActiveServantMissionStartTicksOffset), row.StartTicks);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(Layout.ActiveServantMissionLengthOffset), row.LengthSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.ActiveServantMissionDataIdOffset), row.DataSlot);
        WriteNetworkedEntity(dest.Slice(Layout.ActiveServantMissionServant1Offset), row.Servant1);
        WriteNetworkedEntity(dest.Slice(Layout.ActiveServantMissionServant2Offset), row.Servant2);
        WriteNetworkedEntity(dest.Slice(Layout.ActiveServantMissionServant3Offset), row.Servant3);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.ActiveServantMissionCountOffset), row.Count);
    }

    /// <summary>Decode a mission row.</summary>
    internal static MissionRow DecodeRow(ReadOnlySpan<byte> src) => new()
    {
        MissionGuid = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(Layout.ActiveServantMissionMissionIdOffset)),
        StartTicks = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(Layout.ActiveServantMissionStartTicksOffset)),
        LengthSeconds = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(Layout.ActiveServantMissionLengthOffset)),
        DataSlot = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(Layout.ActiveServantMissionDataIdOffset)),
        Servant1 = ReadEntity(src.Slice(Layout.ActiveServantMissionServant1Offset + Layout.NetworkedEntityEntityOffset)),
        Servant2 = ReadEntity(src.Slice(Layout.ActiveServantMissionServant2Offset + Layout.NetworkedEntityEntityOffset)),
        Servant3 = ReadEntity(src.Slice(Layout.ActiveServantMissionServant3Offset + Layout.NetworkedEntityEntityOffset)),
        Count = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(Layout.ActiveServantMissionCountOffset)),
    };

    /// <summary>A <c>NetworkedEntity</c> the game's send writes: the server entity, not waiting for sync.</summary>
    internal static void WriteNetworkedEntity(Span<byte> dest, CastleId entity)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.NetworkedEntityEntityOffset), entity.Index);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.NetworkedEntityEntityOffset + 4), entity.Version);
        dest[Layout.NetworkedEntityWaitingForSyncOffset] = 0;
    }

    /// <summary>An <c>Entity</c> (index, version) at the start of <paramref name="src"/>.</summary>
    internal static CastleId ReadEntity(ReadOnlySpan<byte> src) =>
        new(BinaryPrimitives.ReadInt32LittleEndian(src), BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4)));

    /// <summary>
    /// Bytes between a <c>FixedList</c>'s length and its first element: the Collections rule
    /// <c>max(0, min(6, lowestSetBit(elementSize) - 2))</c>.
    /// </summary>
    internal static int FixedListPadding(int elementSize)
    {
        if (elementSize <= 0) throw new ArgumentOutOfRangeException(nameof(elementSize));
        int lowest = elementSize & -elementSize;
        return Math.Max(0, Math.Min(6, lowest - 2));
    }

    /// <summary>Offset of the first throne list entry inside the list bytes.</summary>
    internal static int ResponseFirstEntryOffset => 2 + FixedListPadding(Layout.ServantInfoEntryStride);

    /// <summary>How many entries the throne list holds (36).</summary>
    internal static int ResponseCapacity => (ResponseListBytes - ResponseFirstEntryOffset) / Layout.ServantInfoEntryStride;

    /// <summary>One throne list entry, the fields <c>ServantInfoEvent.Response.Entry.Create</c> fills.</summary>
    internal sealed class ThroneEntry
    {
        public byte[] NameBytes = new byte[64];
        public byte[] NetworkId = new byte[12];
        public long InjuryEndTicks;
        public float HuntProficiency;
        public float LootFactor;
        public int Perk0;
        public int Perk1;
        public int Injury;
        public ServantState State;
    }

    /// <summary>Encode one entry into <paramref name="dest"/> (<see cref="Layout.ServantInfoEntryStride"/> bytes).</summary>
    internal static void EncodeEntry(ThroneEntry e, Span<byte> dest)
    {
        if (dest.Length < Layout.ServantInfoEntryStride)
            throw new ArgumentException("an entry needs " + Layout.ServantInfoEntryStride + " bytes", nameof(dest));
        dest.Slice(0, Layout.ServantInfoEntryStride).Clear();
        e.NameBytes.AsSpan(0, Math.Min(64, e.NameBytes.Length)).CopyTo(dest.Slice(Layout.ServantInfoEntryNameOffset));
        e.NetworkId.AsSpan(0, Math.Min(12, e.NetworkId.Length)).CopyTo(dest.Slice(Layout.ServantInfoEntryNetworkIdOffset));
        BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(Layout.ServantInfoEntryInjuryEndTicksOffset), e.InjuryEndTicks);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(Layout.ServantInfoEntryHuntProficiencyOffset), e.HuntProficiency);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(Layout.ServantInfoEntryLootFactorOffset), e.LootFactor);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.ServantInfoEntryPerk0Offset), e.Perk0);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.ServantInfoEntryPerk1Offset), e.Perk1);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(Layout.ServantInfoEntryInjuryOffset), e.Injury);
        dest[Layout.ServantInfoEntryStateOffset] = (byte)e.State;
    }

    /// <summary>
    /// Write a whole throne list into <paramref name="list"/> (the 4096 list bytes): length, zero
    /// padding, the entries, zeros after the last. Entries past the capacity are dropped; the
    /// count written is returned.
    /// </summary>
    internal static int WriteResponseList(Span<byte> list, IReadOnlyList<ThroneEntry> entries)
    {
        if (list.Length < ResponseListBytes)
            throw new ArgumentException("the throne list is " + ResponseListBytes + " bytes", nameof(list));
        int n = Math.Min(entries.Count, ResponseCapacity);
        list.Slice(0, ResponseListBytes).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(list, (ushort)n);
        int at = ResponseFirstEntryOffset;
        for (int i = 0; i < n; i++, at += Layout.ServantInfoEntryStride)
            EncodeEntry(entries[i], list.Slice(at, Layout.ServantInfoEntryStride));
        return n;
    }

    /// <summary>The list's length and each entry's NetworkId and state (to log and to test).</summary>
    internal static List<(byte[] NetworkId, ServantState State)> ReadResponseList(ReadOnlySpan<byte> list)
    {
        int n = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(list), ResponseCapacity);
        var rows = new List<(byte[], ServantState)>(n);
        int at = ResponseFirstEntryOffset;
        for (int i = 0; i < n; i++, at += Layout.ServantInfoEntryStride)
        {
            var id = list.Slice(at + Layout.ServantInfoEntryNetworkIdOffset, 12).ToArray();
            rows.Add((id, (ServantState)list[at + Layout.ServantInfoEntryStateOffset]));
        }
        return rows;
    }

    /// <summary>Two 12-byte NetworkIds are the same id (index, generation and type bytes).</summary>
    internal static bool SameNetworkId(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        BinaryPrimitives.ReadInt32LittleEndian(a.Slice(Layout.NetworkIdNormalIndexOffset)) == BinaryPrimitives.ReadInt32LittleEndian(b.Slice(Layout.NetworkIdNormalIndexOffset))
        && a[Layout.NetworkIdNormalGenerationOffset] == b[Layout.NetworkIdNormalGenerationOffset]
        && a[Layout.NetworkIdTypeOffset] == b[Layout.NetworkIdTypeOffset];

    /// <summary>A zero NetworkId (an empty servant slot of the client's send).</summary>
    internal static bool IsEmptyNetworkId(ReadOnlySpan<byte> id) =>
        BinaryPrimitives.ReadInt32LittleEndian(id.Slice(Layout.NetworkIdNormalIndexOffset)) == 0
        && id[Layout.NetworkIdNormalGenerationOffset] == 0 && id[Layout.NetworkIdTypeOffset] == 0;

    /// <summary>Decode a <c>MapZoneId</c> (three bytes).</summary>
    internal static MissionZone ReadZone(ReadOnlySpan<byte> src) =>
        new((sbyte)src[Layout.MapZoneIdChunkCoordinateOffset], (sbyte)src[Layout.MapZoneIdChunkCoordinateOffset + 1], src[Layout.MapZoneIdZoneIdOffset]);

    /// <summary>The zone byte of the zone at <paramref name="zoneIndex"/> in its chunk (index plus one).</summary>
    internal static byte ZoneIdOf(byte zoneIndex) => (byte)(zoneIndex + 1);

    /// <summary>True when a discovered-zones buffer (3 bytes a zone) holds <paramref name="zone"/>.</summary>
    internal static bool HasDiscovered(ReadOnlySpan<byte> discovered, int length, MissionZone zone)
    {
        int stride = Layout.DiscoveredMapZoneElementStride;
        for (int i = 0; i < length && (i + 1) * stride <= discovered.Length; i++)
            if (ReadZone(discovered.Slice(i * stride, stride)) == zone) return true;
        return false;
    }
}
