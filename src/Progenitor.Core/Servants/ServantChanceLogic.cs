using System;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Servants;

/// <summary>One servant of a party as the success chance reads it (the throne entry's fields).</summary>
internal sealed class ChanceEntry
{
    /// <summary>The servant.</summary>
    public CastleId Servant;

    /// <summary>The game counts only entries with a valid network id (an existing servant).</summary>
    public bool Valid = true;

    /// <summary>Hunt proficiency (<see cref="ServantLogic.HuntProficiency"/>).</summary>
    public float Proficiency;

    /// <summary>The servant's first and second perk (<c>PerksBuffer[0]</c>, <c>[1]</c>); 0 when none.</summary>
    public int Perk0;

    /// <inheritdoc cref="Perk0"/>
    public int Perk1;
}

/// <summary>The mission-side facts of the success chance.</summary>
internal sealed class ChanceMission
{
    /// <summary><c>MissionData.MissionDifficulty</c>.</summary>
    public int Difficulty;

    /// <summary>The mission prefab's <c>PerksBuffer</c>, in order.</summary>
    public IReadOnlyList<int> Perks = Array.Empty<int>();

    /// <summary><c>ServantMissionSettingsSingleton.StaticPartyBonus</c>.</summary>
    public int StaticPartyBonus;

    /// <summary>The length choice's <c>ServantMissionSetting.SuccessRateBonus</c>.</summary>
    public float SuccessRateBonus;

    /// <summary>A perk prefab's <c>PerkData</c> (loot value, power value), null when the prefab has none.</summary>
    public Func<int, (float Loot, int Power)?> PerkData = _ => null;
}

/// <summary>
/// The servant hunt success chance, game-free: the same arithmetic, in the same order and the same
/// float/double widths, as the game's <c>ServantHelper.GetMissionSuccessChanceForServant_Client</c>
/// (the server form forwards to it with no assigned-servant filter, the throne passes the selected
/// servants, so both reduce to "the party").
/// </summary>
internal static class ServantChanceLogic
{
    /// <summary>The divisor of the shortfall's square.</summary>
    internal const double ShortfallDivisor = 15000.0;

    /// <summary>
    /// The chance of <paramref name="party"/>[<paramref name="current"/>]. For every valid entry E
    /// of the party: each mission perk P is matched against E's first and then second perk; a match
    /// whose P is not used yet marks it used and adds its <c>PerkData</c> (power to the reduction,
    /// loot to the loot bonus, which starts at 1). Every valid E other than the current servant
    /// adds the static party bonus. Then <c>x = proficiency + party - (difficulty - reduction)</c>.
    /// <c>x &gt;= 0</c> is 1; otherwise <c>(float)(1 - |x|² / 15000) + successRateBonus</c>, clamped.
    /// </summary>
    internal static ServantChance Compute(ChanceMission mission, IReadOnlyList<ChanceEntry> party, int current, float? cap = null)
    {
        if (mission == null) throw new ArgumentNullException(nameof(mission));
        if (party == null) throw new ArgumentNullException(nameof(party));
        if (current < 0 || current >= party.Count) throw new ArgumentOutOfRangeException(nameof(current));
        var self = party[current];
        int partyPower = 0;
        int reduced = 0;
        float lootBonus = 1f;
        var used = new List<int>();
        foreach (var e in party)
        {
            if (!e.Valid) continue;
            foreach (int p in mission.Perks)
            {
                AddPerk(mission, used, p, e.Perk0, ref reduced, ref lootBonus);
                AddPerk(mission, used, p, e.Perk1, ref reduced, ref lootBonus);
            }
            if (e.Servant != self.Servant) partyPower += mission.StaticPartyBonus;
        }
        float vanilla = Chance(self.Proficiency, partyPower, mission.Difficulty, reduced, mission.SuccessRateBonus);
        return new ServantChance
        {
            Servant = self.Servant,
            Vanilla = vanilla,
            MaxSuccess = cap,
            Applied = Applied(vanilla, cap),
            Proficiency = (int)self.Proficiency,
            PartyBonus = partyPower,
            MissionDifficulty = mission.Difficulty,
            PerkReduction = reduced,
            PerkLootBonus = lootBonus,
            SuccessRateBonus = mission.SuccessRateBonus,
        };
    }

    /// <summary>Every party member's chance, in order.</summary>
    internal static List<ServantChance> ComputeAll(ChanceMission mission, IReadOnlyList<ChanceEntry> party, float? cap = null)
    {
        var list = new List<ServantChance>(party.Count);
        for (int i = 0; i < party.Count; i++)
            if (party[i].Valid) list.Add(Compute(mission, party, i, cap));
        return list;
    }

    /// <summary>The final step: the shortfall against the reduced difficulty, squared over 15000, plus the length's bonus.</summary>
    internal static float Chance(float proficiency, int partyPower, int difficulty, int reduced, float successRateBonus)
    {
        float x = (proficiency + (float)partyPower) - (float)(difficulty - reduced);
        if (x >= 0f) return 1f;
        float v = (float)(1.0 - Math.Pow(Math.Abs(x), 2.0) / ShortfallDivisor) + successRateBonus;
        if (v < 0f) return 0f;
        return v > 1f ? 1f : v;
    }

    /// <summary><c>min(vanilla, cap)</c>; the vanilla chance when there is no cap.</summary>
    internal static float Applied(float vanilla, float? cap) => cap == null ? vanilla : Math.Min(vanilla, cap.Value);

    /// <summary>
    /// <paramref name="returned"/> with the chance read for <paramref name="rowServant"/> (the
    /// servant the hunt row named) before the roll; unchanged when none was read.
    /// </summary>
    internal static ReturnedServant WithRoll(ReturnedServant returned, CastleId rowServant, IReadOnlyList<ServantChance>? chances, bool forced = false)
    {
        ServantChance? chance = null;
        if (chances != null)
            foreach (var c in chances)
                if (c.Servant == rowServant) { chance = c; break; }
        if (chance == null && !forced) return returned;
        return new ReturnedServant
        {
            Servant = returned.Servant,
            Coffin = returned.Coffin,
            Name = returned.Name,
            Outcome = returned.Outcome,
            InjuryEndsUtc = returned.InjuryEndsUtc,
            Inventory = returned.Inventory,
            Loot = returned.Loot,
            LootIsHaul = returned.LootIsHaul,
            Chance = chance,
            CapForcedFailure = forced,
        };
    }

    private static void AddPerk(ChanceMission mission, List<int> used, int missionPerk, int servantPerk, ref int reduced, ref float loot)
    {
        if (servantPerk != missionPerk || used.Contains(missionPerk)) return;
        used.Add(missionPerk);
        var data = mission.PerkData(missionPerk);
        if (data == null) return;
        reduced += data.Value.Power;
        loot = data.Value.Loot + loot;
    }
}
