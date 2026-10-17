using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Servants;

/// <summary>
/// The success-chance half of <see cref="ServantRuntime"/>: the raw reads that feed the game-free
/// <see cref="ServantChanceLogic"/> (the mission prefab's difficulty and perks, each perk prefab's
/// <c>PerkData</c>, the settings singleton's party bonus, the length choice's success bonus, each
/// servant's <c>ServantPower</c> and perks), and the "Hunt in X started." notice the game's own send
/// raises. Reads only, at the declared layout constants; no game method is called.
/// </summary>
internal static unsafe partial class ServantRuntime
{
    /// <summary>The mission-side chance facts for a mission and length choice, or null with <paramref name="why"/>.</summary>
    internal static ChanceMission? ReadChanceMission(World world, EntityManager em, int missionGuid, int dataSlot, out string why)
    {
        why = "";
        if (missionGuid == 0 || !PrefabLookup.TryGetEntity(world, new PrefabGUID(missionGuid), out var prefab))
        {
            why = "mission " + missionGuid + " is not a prefab";
            return null;
        }
        byte* md = AiRuntime.RO<MissionData>(em, prefab, Layout.MissionDataServantSlotsOffset + 4);
        if (md == null)
        {
            why = "mission " + missionGuid + " has no MissionData";
            return null;
        }
        var perks = new List<int>();
        byte* pb = AiRuntime.Buffer<PerksBuffer>(em, prefab, Layout.PerksBufferStride, write: false, out int n);
        for (int i = 0; pb != null && i < n && i < 64; i++) perks.Add(*(int*)(pb + i * Layout.PerksBufferStride));

        var settings = Singleton<ServantMissionSettingsSingleton>(em, ref _missionSettings);
        byte* sp = settings == Entity.Null ? null
            : AiRuntime.RO<ServantMissionSettingsSingleton>(em, settings, Layout.ServantMissionSettingsMissionBuffOffset + 4);
        int settingCount = 0;
        byte* rows = settings == Entity.Null ? null
            : AiRuntime.Buffer<ServantMissionSetting>(em, settings, Layout.ServantMissionSettingStride, write: false, out settingCount);
        if (sp == null || rows == null)
        {
            why = "the game's servant mission settings could not be read";
            return null;
        }
        if (dataSlot < 0 || dataSlot >= settingCount)
        {
            why = "hunt length choice " + dataSlot + " is not one of the game's " + settingCount + " mission settings";
            return null;
        }
        float bonus = *(float*)(rows + dataSlot * Layout.ServantMissionSettingStride + Layout.ServantMissionSettingSuccessRateBonusOffset);
        int party = *(int*)(sp + Layout.ServantMissionSettingsStaticPartyBonusOffset);

        var perkData = new Dictionary<int, (float, int)?>();
        return new ChanceMission
        {
            Difficulty = *(int*)(md + Layout.MissionDataMissionDifficultyOffset),
            Perks = perks,
            StaticPartyBonus = party,
            SuccessRateBonus = bonus,
            PerkData = guid =>
            {
                if (perkData.TryGetValue(guid, out var cached)) return cached;
                (float, int)? value = null;
                if (guid != 0 && PrefabLookup.TryGetEntity(world, new PrefabGUID(guid), out var perk))
                {
                    byte* p = AiRuntime.RO<PerkData>(em, perk, Layout.PerkDataPowerValueOffset + 4);
                    if (p != null) value = (*(float*)(p + Layout.PerkDataLootValueOffset), *(int*)(p + Layout.PerkDataPowerValueOffset));
                }
                perkData[guid] = value;
                return value;
            },
        };
    }

    /// <summary>
    /// The party as the game's return builds it: one entry per servant entity that exists (the
    /// game counts entries with a valid network id), with proficiency from its live
    /// <c>ServantPower</c> and its first two perks. A servant without <c>ServantPower</c> still counts
    /// for the others' party bonus and perks, but gets no chance of its own (the game skips it).
    /// </summary>
    internal static List<ChanceEntry> PartyEntries(EntityManager em, IEnumerable<CastleId> servants, out HashSet<CastleId> noPower)
    {
        var list = new List<ChanceEntry>();
        noPower = new HashSet<CastleId>();
        foreach (var id in servants)
        {
            var s = id.ToEntity();
            var e = new ChanceEntry { Servant = id, Valid = !id.IsNone && em.Exists(s) };
            if (e.Valid)
            {
                byte* pw = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerExpertiseOffset + 4);
                if (pw == null) noPower.Add(id);
                float gear = pw == null ? 0 : *(float*)(pw + Layout.ServantPowerGearLevelOffset);
                float expertise = pw == null ? 0 : *(float*)(pw + Layout.ServantPowerExpertiseOffset);
                e.Proficiency = ServantLogic.HuntProficiency(gear, expertise);
                byte* perks = AiRuntime.Buffer<PerksBuffer>(em, s, Layout.PerksBufferStride, write: false, out int n);
                if (perks != null && n > 0) e.Perk0 = *(int*)perks;
                if (perks != null && n > 1) e.Perk1 = *(int*)(perks + Layout.PerksBufferStride);
            }
            list.Add(e);
        }
        return list;
    }

    /// <summary>Each servant's chance (in order, skipping the ones the game would skip); empty with <paramref name="why"/> when unreadable.</summary>
    internal static List<ServantChance> ChancesFor(World world, EntityManager em, int missionGuid, int dataSlot, IEnumerable<CastleId> servants, float? cap, out string why)
    {
        var list = new List<ServantChance>();
        try
        {
            var mission = ReadChanceMission(world, em, missionGuid, dataSlot, out why);
            if (mission == null) return list;
            var party = PartyEntries(em, servants, out var noPower);
            for (int i = 0; i < party.Count; i++)
                if (party[i].Valid && !noPower.Contains(party[i].Servant))
                    list.Add(ServantChanceLogic.Compute(mission, party, i, cap));
        }
        catch (Exception e)
        {
            why = "the chance could not be read: " + e.Message;
            list.Clear();
        }
        return list;
    }

    /// <summary><c>ctx.Servants.Chance</c>: the chances of a prospective send, and whether <c>Send</c> would accept it now.</summary>
    internal static ServantChanceResult Chance(World world, ServantChanceRequest req, string by)
    {
        var em = world.EntityManager;
        var check = Send(world, new ServantSendRequest
        {
            Heart = req.Heart,
            Servants = req.Servants,
            MissionGuid = req.MissionGuid,
            DataSlot = req.DataSlot,
            User = req.User,
            CheckDiscovered = req.CheckDiscovered,
            Epoch = req.Epoch,
            Announce = false,
        }, by, dryRun: true, log: false);

        bool epochOk = req.Epoch == 0 || req.Epoch == WorldEpoch.Current;
        var chances = new List<ServantChance>();
        string why = "";
        bool countOk = req.Servants.Count >= 1 && req.Servants.Count <= ServantLogic.MaxServantsPerHunt;
        if (epochOk && countOk)
            chances = ChancesFor(world, em, req.MissionGuid, req.DataSlot, req.Servants, req.MaxSuccess, out why);
        bool ok = chances.Count == req.Servants.Count && chances.Count > 0;
        return new ServantChanceResult
        {
            Ok = ok,
            Refusal = check.Ok ? ServantSendRefusal.None : check.Refusal,
            Reason = !check.Ok ? check.Reason : ok ? "" : (why.Length > 0 ? why : "a servant's power could not be read"),
            Heart = req.Heart,
            MissionGuid = req.MissionGuid,
            DataSlot = req.DataSlot,
            Chances = ok ? chances : Array.Empty<ServantChance>(),
        };
    }

    // ---- the "Hunt in X started." notice ----------------------------------------------------------

    /// <summary>
    /// Raise <c>ServantMissionStartedEvent{MissionId}</c> to every connected user whose character is
    /// allied with the castle heart (the game's own send raises it to the sender's allied users),
    /// plus <paramref name="sender"/> when connected. One network event entity with the shape chat
    /// messages use (<c>NetworkEventType</c>, the event component, <c>SendNetworkEventTag</c>,
    /// <c>SendEventToUsers</c> with one bit per user); the event's only field is written raw at its
    /// declared offset, so no generic over the event type is instantiated. Returns how many users
    /// it went to; 0 on any failure (logged), which never fails the send.
    /// </summary>
    internal static int AnnounceStarted(World world, EntityManager em, Entity heart, int missionGuid, Entity sender)
    {
        try
        {
            var mask = default(UserBitMask128);
            int users = 0;
            bool heartTeam = AiRuntime.TryTeam(em, heart, out int hv, out int hf);
            Dictionary<int, AiFactionInfo>? table = null;
            var connected = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<ConnectedUser>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            foreach (var u in connected)
            {
                if (!Progenitor.Chat.Chat.TryConnectedIndex(em, u, out int index)) continue;
                bool include = u == sender;
                if (!include && heartTeam)
                {
                    var character = em.GetComponentData<User>(u).LocalCharacter._Entity;
                    if (AiRuntime.TryTeam(em, character, out int cv, out int cf))
                    {
                        table ??= AiRuntime.Factions(world);
                        var t = table;
                        include = FactionMath.IsAllies(cv, cf, hv, hf, i => t.TryGetValue(i, out var info) ? (info.Membership, info.Friendly) : null) == true;
                    }
                }
                if (!include) continue;
                mask.SetUserBit(index);
                users++;
            }
            if (users == 0) return 0;

            var ev = em.CreateEntity(
                ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<ServantMissionStartedEvent>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendEventToUsers>(ComponentType.AccessMode.ReadWrite));
            em.SetComponentData(ev, new NetworkEventType { EventId = Layout.ServantMissionStartedEventId, IsAdminEvent = false, IsDebugEvent = false });
            byte* p = AiRuntime.RW<ServantMissionStartedEvent>(em, ev, Layout.ServantMissionStartedEventMissionIdOffset + 4);
            *(int*)(p + Layout.ServantMissionStartedEventMissionIdOffset) = missionGuid;
            em.SetComponentData(ev, new SendEventToUsers { Users = mask });
            return users;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: the \"hunt started\" notice for mission " + missionGuid + " could not be raised: " + e.Message);
            return 0;
        }
    }
}
