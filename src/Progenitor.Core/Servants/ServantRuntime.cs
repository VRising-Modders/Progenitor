using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Shared.Systems;
using ProjectM.Terrain;
using Progenitor.Ai;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Inventory;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Servants;

/// <summary>
/// The server half of <c>ctx.Servants</c>: raw reads of the heart's hunt record, the coffins and
/// the servants; the send (the writes the game's own throne send makes); the return detector; and
/// the experimental throne list swap. Installed by Core on the server; <see cref="Uninstall"/>
/// drops every hook and the provider.
///
/// <para>Seams: <see cref="SystemHooks"/> Before/After on the managed systems
/// <c>ServantMissionUpdateSystem</c> (returns), <c>ServantMissionActionSystem</c> (routing a send
/// from a swapped list) and <c>ServantInfoEventSystem_Server</c> (the list
/// swap). No game method is patched or called with a by-ref struct; the success-chance methods
/// are never touched.</para>
/// </summary>
internal static unsafe class ServantRuntime
{
    internal static readonly ServantListRegistry Lists = new();

    private const long HeartsRefreshTicks = ServantLogic.TicksPerSecond;      // re-list the hearts with hunts once a second
    private const long DueSlackTicks = 2 * ServantLogic.TicksPerSecond;      // the game reads the clock after Progenitor
    private const int ConfirmFrames = 60;

    private static IDisposable? _updBefore, _updAfter, _actBefore, _infoBefore, _infoAfter;
    private static bool _installed;

    // Hearts with at least one hunt, refreshed once a second or when a send happened.
    private static readonly List<Entity> MissionHearts = new();
    private static long _heartsStamp;
    private static bool _heartsDirty = true;
    private static int _heartsEpoch = int.MinValue;

    // Rows snapshotted before the update system ran, per heart, for the hearts with a hunt due.
    private static readonly Dictionary<CastleId, List<MissionRow>> Due = new();

    // Pending list swaps: user index -> request facts (Before the info system), applied After.
    private static readonly Dictionary<int, (CastleId User, CastleId Heart, byte[] Throne, IReadOnlyList<CastleId> Hearts)> PendingSwaps = new();

    // Sends to confirm a second later: servants, the buff that should be on them, the row they are in.
    private static readonly List<(long DueTicks, List<CastleId> Servants, int Buff, CastleId Heart, MissionRow Row)> Confirm = new();
    private const long ConfirmAfterTicks = ServantLogic.TicksPerSecond;

    // Returns seen after the update system ran, published once the game has finished with them: the
    // servants that died are destroyed and the survivors' mission buffs are gone (both happen a
    // moment after the return), or at the latest after ReturnMaxWaitTicks.
    private static readonly List<(CastleId Heart, MissionRow Row, long SeenTicks, Dictionary<CastleId, Entity> Coffins)> PendingReturns = new();
    private const long ReturnMinWaitTicks = ServantLogic.TicksPerSecond / 2;
    private const long ReturnMaxWaitTicks = 5 * ServantLogic.TicksPerSecond;

    // Caches per world epoch.
    private static int _cacheEpoch = int.MinValue;
    private static Entity _debugSettings, _missionSettings;
    private static List<(MissionZone Zone, int Mission)>? _zones;

    internal static bool Installed => _installed;

    internal static void Install()
    {
        if (!Plugin.IsServerProcess || _installed) return;
        Lists.Log = line => Plugin.Logger?.LogInfo(line);
        Lists.Unregistered = () => PendingSwaps.Clear(); // holds the provider's lists: never past its unregistration
        _updBefore = SystemHooks.Before<ServantMissionUpdateSystem>(BeforeUpdate);
        _updAfter = SystemHooks.After<ServantMissionUpdateSystem>(AfterUpdate);
        _actBefore = SystemHooks.Before<ServantMissionActionSystem>(BeforeAction);
        _infoBefore = SystemHooks.Before<ServantInfoEventSystem_Server>(BeforeInfo);
        _infoAfter = SystemHooks.After<ServantInfoEventSystem_Server>(AfterInfo);
        Plugin.Frame += Tick;
        _installed = true;
        Plugin.Logger?.LogInfo("Servants: installed (server; returns, sends and the experimental throne list hook)");
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        Plugin.Frame -= Tick;
        foreach (var h in new[] { _updBefore, _updAfter, _actBefore, _infoBefore, _infoAfter })
        {
            try { h?.Dispose(); } catch { }
        }
        _updBefore = _updAfter = _actBefore = _infoBefore = _infoAfter = null;
        Lists.Clear();
        Lists.Log = null;
        Lists.Unregistered = null;
        MissionHearts.Clear();
        Due.Clear();
        PendingSwaps.Clear();
        Confirm.Clear();
        PendingReturns.Clear();
        _zones = null;
        _cacheEpoch = int.MinValue;
        _heartsEpoch = int.MinValue;
    }

    // ---- reads ------------------------------------------------------------------------------------

    internal static World RequireWorld(string op)
    {
        if (!Plugin.IsServerProcess || !_installed)
            throw new InvalidOperationException("ctx.Servants." + op + " runs on the server side only; hunts are server-authoritative.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Servants." + op + " must run on the game main thread.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("ctx.Servants." + op + ": the server world is not ready.");
        return world;
    }

    private static void RefreshCaches(EntityManager em)
    {
        int epoch = WorldEpoch.Current;
        if (epoch == _cacheEpoch) return;
        _cacheEpoch = epoch;
        _debugSettings = Entity.Null;
        _missionSettings = Entity.Null;
        _zones = null;
        Lists.ForgetShown();
    }

    private static Entity Singleton<T>(EntityManager em, ref Entity cached)
    {
        RefreshCaches(em);
        if (cached != Entity.Null && em.Exists(cached) && em.HasComponent(cached, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly)))
            return cached;
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
        var found = Query.Entities(em, includeDisabled: false, includeSystems: false, type);
        if (found.Count == 0) found = Query.Entities(em, includeDisabled: true, includeSystems: true, type);
        cached = found.Count == 0 ? Entity.Null : found[0];
        return cached;
    }

    /// <summary>The game's debug switches the send reads: progression unlocked, servant timers off.</summary>
    private static (bool SkipProgression, bool TimersDisabled) DebugFlags(EntityManager em)
    {
        var e = Singleton<SyncedServerDebugSettings>(em, ref _debugSettings);
        int need = Math.Max(Layout.SyncedServerDebugSettingsAllProgressionUnlockedOffset, Layout.SyncedServerDebugSettingsServantTimersDisabledOffset) + 1;
        byte* p = e == Entity.Null ? null : AiRuntime.RO<SyncedServerDebugSettings>(em, e, need);
        if (p == null) return (false, false);
        return (p[Layout.SyncedServerDebugSettingsAllProgressionUnlockedOffset] != 0, p[Layout.SyncedServerDebugSettingsServantTimersDisabledOffset] != 0);
    }

    /// <summary>The castle heart of a heart, a throne or anything else connected to one.</summary>
    internal static Entity HeartOf(EntityManager em, Entity entity)
    {
        if (entity == Entity.Null || !em.Exists(entity)) return Entity.Null;
        if (em.HasComponent(entity, ComponentIndex.Of<CastleHeart>(ComponentType.AccessMode.ReadOnly))) return entity;
        byte* p = AiRuntime.RO<CastleHeartConnection>(em, entity, Layout.CastleHeartConnectionEntityOffset + 8);
        if (p == null) return Entity.Null;
        var heart = AiRuntime.ReadEntity(p + Layout.CastleHeartConnectionEntityOffset);
        return heart != Entity.Null && em.Exists(heart) ? heart : Entity.Null;
    }

    private static bool HasMissionRecord(EntityManager em, Entity heart) =>
        heart != Entity.Null && em.Exists(heart)
        && em.HasComponent(heart, ComponentIndex.Of<ActiveServantMission>(ComponentType.AccessMode.ReadOnly));

    /// <summary>The heart's hunt record, decoded.</summary>
    internal static List<MissionRow> ReadRows(EntityManager em, Entity heart)
    {
        var rows = new List<MissionRow>();
        int stride = Layout.ActiveServantMissionStride;
        byte* raw = AiRuntime.Buffer<ActiveServantMission>(em, heart, stride, write: false, out int length);
        for (int i = 0; raw != null && i < length && i < 256; i++)
            rows.Add(ServantLogic.DecodeRow(new ReadOnlySpan<byte>(raw + i * stride, stride)));
        return rows;
    }

    /// <summary>Every map zone and its mission, cached per world load.</summary>
    private static List<(MissionZone Zone, int Mission)> Zones(EntityManager em)
    {
        RefreshCaches(em);
        if (_zones != null) return _zones;
        var list = new List<(MissionZone, int)>();
        var rows = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<MapZoneData>(ComponentType.AccessMode.ReadOnly));
        foreach (var e in rows)
        {
            byte* p = AiRuntime.RO<MapZoneData>(em, e, Layout.MapZoneDataServantMissionAssetOffset + 4);
            if (p == null) continue;
            int mission = *(int*)(p + Layout.MapZoneDataServantMissionAssetOffset);
            var zone = new MissionZone((sbyte)p[Layout.MapZoneDataChunkCoordinateOffset], (sbyte)p[Layout.MapZoneDataChunkCoordinateOffset + 1],
                ServantLogic.ZoneIdOf(p[Layout.MapZoneDataZoneIndexOffset]));
            list.Add((zone, mission));
        }
        _zones = list;
        return list;
    }

    internal static List<MissionZone> ZonesFor(EntityManager em, int mission)
    {
        var list = new List<MissionZone>();
        if (mission == 0) return list;
        foreach (var (zone, m) in Zones(em))
            if (m == mission) list.Add(zone);
        return list;
    }

    internal static int MissionFor(EntityManager em, MissionZone zone)
    {
        foreach (var (z, m) in Zones(em))
            if (z == zone) return m;
        return 0;
    }

    private static string PrefabName(World world, int guid)
    {
        if (guid == 0) return "";
        try { return PrefabLookup.TryGetName(world, new PrefabGUID(guid), out var name) ? name : ""; }
        catch { return ""; }
    }

    private static string CoffinName(byte* coffin) =>
        NameplateCodec.Decode(new ReadOnlySpan<byte>(coffin + Layout.ServantCoffinstationNameOffset, NameplateCodec.Size));

    private static int CoffinNeed => Layout.ServantCoffinstationStateOffset + 4;

    /// <summary>Stacks in the servant's inventory; -1 when unreadable.</summary>
    private static int ItemStacks(Entity servant, out InventoryRef inventory, out List<ServantLoot> loot)
    {
        inventory = InventoryRef.For(servant);
        loot = new List<ServantLoot>();
        if (!InventoryRuntime.Installed) return -1;
        try
        {
            var snap = InventoryRuntime.Engine.Read(inventory);
            if (!snap.Ok) return -1;
            var rows = new List<(int, int)>();
            int stacks = 0;
            foreach (var s in snap.Slots)
            {
                if (s.Item == 0 || s.Amount <= 0) continue;
                stacks++;
                rows.Add((s.Item, s.Amount));
            }
            loot = ServantLogic.Loot(rows);
            return stacks;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Every coffin the throne would list for <paramref name="heart"/>, in query order.</summary>
    private static List<Entity> CoffinsOf(EntityManager em, Entity heart)
    {
        var list = new List<Entity>();
        var coffins = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<ServantCoffinstation>(ComponentType.AccessMode.ReadOnly));
        foreach (var c in coffins)
        {
            byte* p = AiRuntime.RO<ServantCoffinstation>(em, c, CoffinNeed);
            if (p == null || !ServantLogic.ThroneLists(*(int*)(p + Layout.ServantCoffinstationStateOffset))) continue;
            if (HeartOf(em, c) == heart) list.Add(c);
        }
        return list;
    }

    private static Entity ServantOfCoffin(byte* coffin) =>
        AiRuntime.ReadEntity(coffin + Layout.ServantCoffinstationConnectedServantOffset + Layout.NetworkedEntityEntityOffset);

    private static Entity CoffinOfServant(EntityManager em, Entity servant)
    {
        byte* p = AiRuntime.RO<ServantConnectedCoffin>(em, servant, Layout.ServantConnectedCoffinEntityOffset + 8);
        if (p == null) return Entity.Null;
        var coffin = AiRuntime.ReadEntity(p + Layout.ServantConnectedCoffinEntityOffset + Layout.NetworkedEntityEntityOffset);
        return coffin != Entity.Null && em.Exists(coffin) ? coffin : Entity.Null;
    }

    private static bool IsOnMission(EntityManager em, Entity servant, out bool isServant)
    {
        byte* p = AiRuntime.RO<ServantData>(em, servant, Layout.ServantDataIsOnMissionOffset + 1);
        isServant = p != null;
        return p != null && p[Layout.ServantDataIsOnMissionOffset] != 0;
    }

    private static bool IsDead(EntityManager em, Entity servant) =>
        em.HasComponent(servant, ComponentIndex.Of<Dead>(ComponentType.AccessMode.ReadOnly));

    private static byte[] NetworkIdOf(EntityManager em, Entity entity)
    {
        var id = new byte[12];
        byte* p = entity == Entity.Null ? null : AiRuntime.RO<NetworkId>(em, entity, Layout.NetworkIdTypeOffset + 1);
        if (p != null) new ReadOnlySpan<byte>(p, 12).CopyTo(id);
        return id;
    }

    /// <summary>One throne entry for a coffin, filled the way the game's <c>Entry.Create</c> fills it.</summary>
    private static ServantLogic.ThroneEntry EntryFor(EntityManager em, Entity coffin, Entity heart, long now, out ServantInfo info, World world)
    {
        byte* c = AiRuntime.RO<ServantCoffinstation>(em, coffin, CoffinNeed);
        var e = new ServantLogic.ThroneEntry();
        var servant = c == null ? Entity.Null : ServantOfCoffin(c);
        bool alive = servant != Entity.Null && em.Exists(servant);
        if (c != null)
        {
            new ReadOnlySpan<byte>(c + Layout.ServantCoffinstationNameOffset, 64).CopyTo(e.NameBytes);
            e.InjuryEndTicks = *(long*)(c + Layout.ServantCoffinstationInjuryEndTicksOffset);
            e.Injury = *(int*)(c + Layout.ServantCoffinstationInjuryOffset);
        }
        float gear = 0, expertise = 0;
        if (alive)
        {
            e.NetworkId = NetworkIdOf(em, servant);
            byte* pw = AiRuntime.RO<ServantPower>(em, servant, Layout.ServantPowerExpertiseOffset + 4);
            if (pw != null)
            {
                gear = *(float*)(pw + Layout.ServantPowerGearLevelOffset);
                expertise = *(float*)(pw + Layout.ServantPowerExpertiseOffset);
            }
            byte* perks = AiRuntime.Buffer<PerksBuffer>(em, servant, Layout.PerksBufferStride, write: false, out int n);
            if (perks != null && n > 0) e.Perk0 = *(int*)perks;
            if (perks != null && n > 1) e.Perk1 = *(int*)(perks + Layout.PerksBufferStride);
        }
        e.HuntProficiency = ServantLogic.HuntProficiency(gear, expertise);
        e.LootFactor = ServantLogic.LootFactor(expertise);
        bool onHunt = false;
        if (alive)
            foreach (var row in ReadRows(em, heart))
                if (row.Names(CastleId.From(servant))) { onHunt = true; break; }
        int stacks = alive ? ItemStacks(servant, out var inv, out _) : -1;
        int state = c == null ? 0 : *(int*)(c + Layout.ServantCoffinstationStateOffset);
        e.State = ServantLogic.StateOf(state, e.InjuryEndTicks, now, onHunt, stacks == 0);
        info = new ServantInfo
        {
            Servant = alive ? CastleId.From(servant) : CastleId.None,
            Coffin = CastleId.From(coffin),
            Heart = CastleId.From(heart),
            Name = c == null ? "" : CoffinName(c),
            State = e.State,
            InjuryEndsUtc = e.InjuryEndTicks > now ? ServantLogic.Utc(e.InjuryEndTicks) : null,
            Injury = e.Injury,
            HuntProficiency = e.HuntProficiency,
            LootFactor = e.LootFactor,
            GearLevel = gear,
            Expertise = expertise,
            Perk0 = e.Perk0,
            Perk1 = e.Perk1,
            Inventory = alive ? InventoryRef.For(servant) : InventoryRef.None,
            ItemStacks = stacks,
            Epoch = WorldEpoch.Current,
        };
        return e;
    }

    internal static IReadOnlyList<ServantInfo> List(World world, Entity throneOrHeart)
    {
        var em = world.EntityManager;
        var heart = HeartOf(em, throneOrHeart);
        var list = new List<ServantInfo>();
        if (heart == Entity.Null) return list;
        long now = DateTime.UtcNow.Ticks;
        foreach (var coffin in CoffinsOf(em, heart))
        {
            EntryFor(em, coffin, heart, now, out var info, world);
            list.Add(info);
        }
        return list;
    }

    internal static IReadOnlyList<ServantMission> Missions(World world, Entity throneOrHeart)
    {
        var em = world.EntityManager;
        var heart = HeartOf(em, throneOrHeart);
        var list = new List<ServantMission>();
        if (heart == Entity.Null) return list;
        var rows = ReadRows(em, heart);
        for (int i = 0; i < rows.Count; i++)
            list.Add(ToMission(world, em, heart, rows[i], i));
        return list;
    }

    private static ServantMission ToMission(World world, EntityManager em, Entity heart, MissionRow r, int index)
    {
        var zones = ZonesFor(em, r.MissionGuid);
        return new ServantMission
        {
            Heart = CastleId.From(heart),
            Row = index,
            MissionGuid = r.MissionGuid,
            MissionName = PrefabName(world, r.MissionGuid),
            Zone = zones.Count > 0 ? zones[0] : MissionZone.None,
            DataSlot = r.DataSlot,
            StartedUtc = ServantLogic.Utc(r.StartTicks),
            LengthSeconds = r.LengthSeconds,
            EndsUtc = ServantLogic.Utc(ServantLogic.CompletionTicks(r.StartTicks, r.LengthSeconds)),
            Servants = new List<CastleId>(r.Servants()),
            Epoch = WorldEpoch.Current,
        };
    }

    // ---- the send ---------------------------------------------------------------------------------

    /// <summary>
    /// Send servants on a hunt with the writes the game's own throne send makes: one row in the
    /// heart's <c>ActiveServantMission</c> record and the mission buff on each servant (whose
    /// script marks it as on a hunt). Validated first with the game's checks; nothing is written
    /// on a refusal.
    /// </summary>
    internal static ServantSendResult Send(World world, ServantSendRequest req, string by)
    {
        var em = world.EntityManager;
        int epoch = WorldEpoch.Current;
        if (req.Epoch != 0 && req.Epoch != epoch)
            return ServantSendResult.Refused(ServantSendRefusal.HeartGone, "the ids are from world load epoch " + req.Epoch
                + "; the world is now in epoch " + epoch + ". Read the heart and servants again.");

        var heart = req.Heart.ToEntity();
        var f = new SendFacts { Heart = req.Heart, MissionGuid = req.MissionGuid, DataSlot = req.DataSlot, CheckDiscovered = req.CheckDiscovered };
        f.HeartOk = em.Exists(heart) && em.HasComponent(heart, ComponentIndex.Of<CastleHeart>(ComponentType.AccessMode.ReadOnly)) && HasMissionRecord(em, heart);
        f.NowTicks = DateTime.UtcNow.Ticks;
        var (skip, timers) = DebugFlags(em);
        f.SkipProgression = skip;
        f.TimersDisabled = timers;
        f.MissionName = PrefabName(world, req.MissionGuid);

        var zones = ZonesFor(em, req.MissionGuid);
        f.ZoneKnown = zones.Count > 0;
        f.Zone = f.ZoneKnown ? zones[0] : MissionZone.None;

        if (f.HeartOk)
        {
            f.Existing = ReadRows(em, heart);
            if (f.CheckDiscovered && !skip)
            {
                var user = req.User.IsNone ? OwnerOf(heart) : req.User.ToEntity();
                f.ProgressionReadable = TryDiscovered(em, user, zones, out bool discovered, out var matched);
                f.Discovered = discovered;
                if (discovered) f.Zone = matched;
            }
        }

        if (req.MissionGuid != 0 && PrefabLookup.TryGetEntity(world, new PrefabGUID(req.MissionGuid), out var prefab))
        {
            byte* md = AiRuntime.RO<MissionData>(em, prefab, Layout.MissionDataServantSlotsOffset + 4);
            if (md != null)
            {
                f.MissionDataKnown = true;
                f.ServantSlots = *(int*)(md + Layout.MissionDataServantSlotsOffset);
            }
        }

        var settings = Singleton<ServantMissionSettingsSingleton>(em, ref _missionSettings);
        int buff = 0;
        if (settings != Entity.Null)
        {
            byte* sp = AiRuntime.RO<ServantMissionSettingsSingleton>(em, settings, Layout.ServantMissionSettingsMissionBuffOffset + 4);
            if (sp != null) buff = *(int*)(sp + Layout.ServantMissionSettingsMissionBuffOffset);
            byte* rows = AiRuntime.Buffer<ServantMissionSetting>(em, settings, Layout.ServantMissionSettingStride, write: false, out int count);
            f.SettingsReadable = rows != null && count > 0 && buff != 0;
            f.SettingCount = rows == null ? 0 : count;
            if (rows != null && req.DataSlot >= 0 && req.DataSlot < count)
                f.SettingLength = *(float*)(rows + req.DataSlot * Layout.ServantMissionSettingStride + Layout.ServantMissionSettingMissionLengthOffset);
        }

        var facts = new List<ServantFacts>();
        foreach (var id in req.Servants)
        {
            var s = id.ToEntity();
            var sf = new ServantFacts { Servant = id };
            sf.Exists = !id.IsNone && em.Exists(s);
            if (sf.Exists)
            {
                sf.OnMission = IsOnMission(em, s, out sf.IsServant);
                sf.Dead = IsDead(em, s);
                sf.ItemStacks = ItemStacks(s, out _, out _);
                var coffin = CoffinOfServant(em, s);
                sf.Coffin = CastleId.From(coffin);
                if (coffin != Entity.Null)
                {
                    byte* c = AiRuntime.RO<ServantCoffinstation>(em, coffin, CoffinNeed);
                    if (c != null)
                    {
                        sf.InjuryEndTicks = *(long*)(c + Layout.ServantCoffinstationInjuryEndTicksOffset);
                        sf.Name = CoffinName(c);
                    }
                    sf.CoffinHeart = CastleId.From(HeartOf(em, coffin));
                }
            }
            facts.Add(sf);
        }
        f.Servants = facts;

        var plan = ServantLogic.PlanSend(f);
        if (!plan.Ok || plan.Row == null)
        {
            Plugin.Logger?.LogInfo("servants: send refused by=" + by + " heart=" + req.Heart + " mission=" + req.MissionGuid
                + " slot=" + req.DataSlot + " (" + plan.Refusal + "): " + plan.Reason);
            return new ServantSendResult
            {
                Ok = false, Refusal = plan.Refusal, Reason = plan.Reason, Heart = req.Heart, MissionGuid = req.MissionGuid,
                Zone = f.Zone, DataSlot = req.DataSlot, Servants = req.Servants,
            };
        }

        // A servant still carrying the mission buff of its last hunt (the game removes it a moment
        // after the return) would make the buff below a no-op: refuse until it is gone.
        foreach (var sf in facts)
            if (AiRuntime.FindBuff(em, sf.Servant.ToEntity(), buff) != Entity.Null)
                return Fail(req, ServantSendRefusal.ServantBusy, "servant " + sf.Servant
                    + " still carries the mission buff of its last hunt (the game removes it a moment after the return); try again shortly", by, log: false);

        // The buff first: if the game does not spawn it for every servant, nothing is in the record
        // yet and the buffs that did go on are taken off again. The buff spawns synchronously on
        // this path (the debug-event route spawns and plays it back in the call).
        var applied = new List<Entity>();
        foreach (var sf in facts)
        {
            var s = sf.Servant.ToEntity();
            string? failure = null;
            try
            {
                AiRuntime.ApplyBuff(world, s, buff);
                if (AiRuntime.FindBuff(em, s, buff) == Entity.Null) failure = "the game did not spawn the mission buff on servant " + sf.Servant;
                else applied.Add(s);
            }
            catch (Exception e)
            {
                failure = "the mission buff could not be applied to servant " + sf.Servant + " (" + e.Message + ")";
            }
            if (failure != null)
            {
                RemoveBuffs(world, applied, buff);
                return Fail(req, ServantSendRefusal.WriteFailed, failure + "; nothing was written", by, log: true);
            }
        }

        var row = plan.Row;
        try
        {
            byte* element = stackalloc byte[Layout.ActiveServantMissionStride];
            ServantLogic.EncodeRow(row, new Span<byte>(element, Layout.ActiveServantMissionStride));
            var type = ComponentIndex.Of<ActiveServantMission>(ComponentType.AccessMode.ReadWrite);
            UnsafeEntityManagerUtility.AddBufferElement(em, heart, type.TypeIndex.Value, element);
        }
        catch (Exception e)
        {
            RemoveBuffs(world, applied, buff);
            return Fail(req, ServantSendRefusal.WriteFailed, "the hunt record could not be written (" + e.Message + "); the mission buffs were removed", by, log: true);
        }

        bool found = false;
        foreach (var back in ReadRows(em, heart))
            if (back.Key == row.Key && back.LengthSeconds == row.LengthSeconds && back.DataSlot == row.DataSlot) { found = true; break; }
        if (!found)
        {
            RemoveBuffs(world, applied, buff);
            Plugin.Logger?.LogError("servants: the hunt record of heart " + req.Heart + " does not read back the row just added (mission "
                + row.MissionGuid + "); the mission buffs were removed. Report this.");
            return Fail(req, ServantSendRefusal.WriteFailed, "the hunt record did not read back the new row; the mission buffs were removed", by, log: false);
        }

        _heartsDirty = true;
        var sent = new List<CastleId>(row.Servants());
        Confirm.Add((DateTime.UtcNow.Ticks + ConfirmAfterTicks, sent, buff, req.Heart, row));
        var result = new ServantSendResult
        {
            Ok = true,
            Heart = req.Heart,
            MissionGuid = row.MissionGuid,
            Zone = f.Zone,
            DataSlot = row.DataSlot,
            LengthSeconds = row.LengthSeconds,
            StartedUtc = ServantLogic.Utc(row.StartTicks),
            Servants = sent,
            BuffApplied = true,
        };
        Plugin.Logger?.LogInfo("servants: sent by=" + by + " heart=" + req.Heart + " mission=" + row.MissionGuid
            + (f.MissionName.Length > 0 ? " (" + f.MissionName + ")" : "") + " zone=" + f.Zone + " slot=" + row.DataSlot
            + " length=" + row.LengthSeconds + "s servants=" + string.Join(",", sent));
        try { EventBus.Publish(new ServantsSent { Result = result, By = by }); }
        catch (Exception e) { Plugin.Logger?.LogWarning("servants: ServantsSent handler threw: " + e.Message); }
        return result;
    }

    private static ServantSendResult Fail(ServantSendRequest req, ServantSendRefusal refusal, string why, string by, bool log)
    {
        if (log) Plugin.Logger?.LogWarning("servants: send failed by=" + by + " heart=" + req.Heart + ": " + why);
        else Plugin.Logger?.LogInfo("servants: send refused by=" + by + " heart=" + req.Heart + " (" + refusal + "): " + why);
        return new ServantSendResult { Ok = false, Refusal = refusal, Reason = why, Heart = req.Heart, MissionGuid = req.MissionGuid, DataSlot = req.DataSlot, Servants = req.Servants };
    }

    private static void RemoveBuffs(World world, List<Entity> servants, int buff)
    {
        foreach (var s in servants)
        {
            try { AiRuntime.RemoveBuff(world, s, buff); }
            catch (Exception e) { Plugin.Logger?.LogWarning("servants: removing the mission buff from " + CastleId.From(s) + " failed: " + e.Message); }
        }
    }

    private static Entity OwnerOf(Entity heart)
    {
        var castle = Progenitor.Castles.Castles.ForHeart(CastleId.From(heart));
        return castle.Enabled ? castle.Owner.User.ToEntity() : Entity.Null;
    }

    /// <summary>Did <paramref name="user"/> discover any of <paramref name="zones"/>? False with false return when unreadable.</summary>
    private static bool TryDiscovered(EntityManager em, Entity user, List<MissionZone> zones, out bool discovered, out MissionZone matched)
    {
        discovered = false;
        matched = MissionZone.None;
        if (user == Entity.Null || !em.Exists(user)) return false;
        byte* pm = AiRuntime.RO<ProgressionMapper>(em, user, Layout.NetworkedEntityWaitingForSyncOffset + 1);
        if (pm == null || pm[Layout.ProgressionMapperEntityOffset + Layout.NetworkedEntityWaitingForSyncOffset] != 0) return false;
        var progression = AiRuntime.ReadEntity(pm + Layout.ProgressionMapperEntityOffset + Layout.NetworkedEntityEntityOffset);
        if (progression == Entity.Null || !em.Exists(progression)) return false;
        int stride = Layout.DiscoveredMapZoneElementStride;
        if (!em.HasComponent(progression, ComponentIndex.Of<DiscoveredMapZoneElement>(ComponentType.AccessMode.ReadOnly))) return false;
        byte* raw = AiRuntime.Buffer<DiscoveredMapZoneElement>(em, progression, stride, write: false, out int length);
        if (raw == null) return true; // readable, nothing discovered
        length = Math.Min(length, 4096);
        var span = new ReadOnlySpan<byte>(raw, length * stride);
        foreach (var z in zones)
            if (ServantLogic.HasDiscovered(span, length, z)) { discovered = true; matched = z; break; }
        return true;
    }

    /// <summary>Send the servants of a return that came back unhurt on the same hunt again.</summary>
    internal static ServantSendResult Resend(World world, ServantsReturned returned, string by)
    {
        var survivors = ServantLogic.Survivors(returned.Servants);
        if (survivors.Count == 0)
            return ServantSendResult.Refused(ServantSendRefusal.ServantCount, "no servant of that hunt came back unhurt");
        return Send(world, new ServantSendRequest
        {
            Heart = returned.Heart,
            Servants = survivors,
            MissionGuid = returned.MissionGuid,
            DataSlot = returned.DataSlot,
            CheckDiscovered = false,
            Epoch = returned.Epoch == 0 ? WorldEpoch.Current : returned.Epoch,
        }, by);
    }

    // ---- per frame --------------------------------------------------------------------------------

    private static void Tick()
    {
        if (Confirm.Count == 0 && PendingReturns.Count == 0) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            Confirm.Clear();
            PendingReturns.Clear();
            return;
        }
        long now = DateTime.UtcNow.Ticks;
        try { ConfirmSends(world.EntityManager, now); }
        catch (Exception e) { Confirm.Clear(); Plugin.Logger?.LogWarning("servants: send confirmation failed: " + e.Message); }
        try { PublishReturns(world, now); }
        catch (Exception e) { PendingReturns.Clear(); Plugin.Logger?.LogWarning("servants: publishing returns failed: " + e.Message); }
    }

    private static void ConfirmSends(EntityManager em, long now)
    {
        for (int i = Confirm.Count - 1; i >= 0; i--)
        {
            var c = Confirm[i];
            if (c.DueTicks > now) continue;
            Confirm.RemoveAt(i);
            bool stillOut = false;
            foreach (var r in ReadRows(em, c.Heart.ToEntity()))
                if (r.Key == c.Row.Key) { stillOut = true; break; }
            if (!stillOut)
            {
                Plugin.Logger?.LogInfo("servants: send of heart " + c.Heart + " mission " + c.Row.MissionGuid + " already returned before the check");
                continue;
            }
            var missing = new List<string>();
            foreach (var id in c.Servants)
            {
                var s = id.ToEntity();
                bool buffed = em.Exists(s) && AiRuntime.FindBuff(em, s, c.Buff) != Entity.Null;
                bool flagged = em.Exists(s) && IsOnMission(em, s, out _);
                if (!buffed || !flagged) missing.Add(id + (buffed ? " (buff on, not marked on a hunt)" : " (no mission buff)"));
            }
            if (missing.Count == 0)
                Plugin.Logger?.LogInfo("servants: send confirmed heart=" + c.Heart + " mission=" + c.Row.MissionGuid + " servants=" + string.Join(",", c.Servants));
            else
                Plugin.Logger?.LogWarning("servants: send of heart " + c.Heart + " mission " + c.Row.MissionGuid + " not confirmed for " + string.Join(", ", missing)
                    + "; the hunt record row stays and the game finishes it on time");
        }
    }

    // ---- returns ----------------------------------------------------------------------------------

    private static void RefreshHearts(EntityManager em, long now)
    {
        int epoch = WorldEpoch.Current;
        if (!_heartsDirty && epoch == _heartsEpoch && now - _heartsStamp < HeartsRefreshTicks) return;
        _heartsDirty = false;
        _heartsEpoch = epoch;
        _heartsStamp = now;
        MissionHearts.Clear();
        var type = ComponentIndex.Of<ActiveServantMission>(ComponentType.AccessMode.ReadOnly);
        foreach (var heart in Query.Entities(em, includeDisabled: true, includeSystems: false, type))
            if (em.GetBufferLength(heart, type.TypeIndex) > 0) MissionHearts.Add(heart);
    }

    private static void BeforeUpdate(SystemHookContext hook)
    {
        Due.Clear();
        if (EventBus.SubscriberCount<ServantsReturned>() == 0) return;
        try
        {
            var em = hook.World.EntityManager;
            long now = DateTime.UtcNow.Ticks;
            bool timers = DebugFlags(em).TimersDisabled;
            // With the game's servant timers off a hunt returns on the next update, so the list of
            // hearts with hunts is re-read every update; otherwise once a second (hunts last minutes).
            if (timers) _heartsDirty = true;
            RefreshHearts(em, now);
            if (MissionHearts.Count == 0) return;
            foreach (var heart in MissionHearts)
            {
                if (!em.Exists(heart)) continue;
                var rows = ReadRows(em, heart);
                foreach (var r in rows)
                    if (ServantLogic.IsDue(r, now, DueSlackTicks, timers)) { Due[CastleId.From(heart)] = rows; break; }
            }
        }
        catch (Exception e)
        {
            Due.Clear();
            Plugin.Logger?.LogWarning("servants: return snapshot failed: " + e.Message);
        }
    }

    private static void AfterUpdate(SystemHookContext hook)
    {
        if (Due.Count == 0) return;
        var world = hook.World;
        var em = world.EntityManager;
        var finished = new List<(Entity Heart, MissionRow Row)>();
        try
        {
            foreach (var kv in Due)
            {
                var heart = kv.Key.ToEntity();
                if (!em.Exists(heart)) continue;
                foreach (var r in ServantLogic.Finished(kv.Value, ReadRows(em, heart)))
                    finished.Add((heart, r));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: return check failed: " + e.Message);
        }
        Due.Clear();
        if (finished.Count == 0) return;
        _heartsDirty = true;
        long now = DateTime.UtcNow.Ticks;
        try
        {
            // Coffins are matched now, while the servants that died still exist (the game destroys
            // them a moment later) and still name their coffin.
            var coffinByServant = CoffinsByServant(em);
            foreach (var (heart, row) in finished)
            {
                var coffins = new Dictionary<CastleId, Entity>();
                foreach (var id in row.Servants())
                {
                    if (!coffinByServant.TryGetValue(id, out var coffin) && em.Exists(id.ToEntity())) coffin = CoffinOfServant(em, id.ToEntity());
                    coffins[id] = coffin;
                }
                PendingReturns.Add((CastleId.From(heart), row, now, coffins));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: queuing a return failed: " + e.Message);
        }
    }

    /// <summary>
    /// Publish the returns the game has finished with: every servant that died is destroyed and
    /// every survivor's mission buff is gone (so <c>Resend</c> from a handler works), or the wait
    /// ran out.
    /// </summary>
    private static void PublishReturns(World world, long now)
    {
        if (PendingReturns.Count == 0) return;
        var em = world.EntityManager;
        var ready = new List<(CastleId Heart, MissionRow Row, long SeenTicks, Dictionary<CastleId, Entity> Coffins)>();
        for (int i = PendingReturns.Count - 1; i >= 0; i--)
        {
            var p = PendingReturns[i];
            long waited = now - p.SeenTicks;
            if (waited < ReturnMinWaitTicks) continue;
            bool settled = true;
            foreach (var id in p.Row.Servants())
            {
                var s = id.ToEntity();
                if (!em.Exists(s) || IsDestroying(em, s)) continue;
                if (IsOnMission(em, s, out _)) { settled = false; break; }
            }
            if (!settled && waited < ReturnMaxWaitTicks) continue;
            PendingReturns.RemoveAt(i);
            ready.Add(p);
        }
        for (int i = ready.Count - 1; i >= 0; i--)
        {
            var (heartId, row, seen, coffins) = ready[i];
            try
            {
                var ev = BuildReturn(world, em, heartId.ToEntity(), row, seen, now, coffins);
                Plugin.Logger?.LogInfo("servants: hunt returned heart=" + ev.Heart + " mission=" + ev.MissionGuid + " zone=" + ev.Zone
                    + " slot=" + ev.DataSlot + " " + Describe(ev));
                EventBus.Publish(ev);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("servants: building or publishing a return failed: " + e.Message);
            }
        }
    }

    /// <summary>Dead, or destroyed and not yet removed (the game's destroy is processed a moment later).</summary>
    private static bool IsDestroying(EntityManager em, Entity servant) =>
        IsDead(em, servant)
        || em.HasComponent(servant, ComponentIndex.Of<DestroyTag>(ComponentType.AccessMode.ReadOnly))
        || em.HasComponent(servant, ComponentIndex.Of<DestroyData>(ComponentType.AccessMode.ReadOnly));

    private static Dictionary<CastleId, Entity> CoffinsByServant(EntityManager em)
    {
        var map = new Dictionary<CastleId, Entity>();
        var coffins = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<ServantCoffinstation>(ComponentType.AccessMode.ReadOnly));
        foreach (var c in coffins)
        {
            byte* p = AiRuntime.RO<ServantCoffinstation>(em, c, CoffinNeed);
            if (p == null) continue;
            var s = ServantOfCoffin(p);
            if (s != Entity.Null) map[CastleId.From(s)] = c;
        }
        return map;
    }

    private static ServantsReturned BuildReturn(World world, EntityManager em, Entity heart, MissionRow row, long seen, long now, Dictionary<CastleId, Entity> coffins)
    {
        var servants = new List<ReturnedServant>();
        foreach (var id in row.Servants())
        {
            var s = id.ToEntity();
            bool exists = em.Exists(s);
            bool dead = exists && IsDestroying(em, s);
            coffins.TryGetValue(id, out var coffin);
            if (coffin != Entity.Null && !em.Exists(coffin)) coffin = Entity.Null;
            if (coffin == Entity.Null && exists) coffin = CoffinOfServant(em, s);
            int state = 0;
            long injury = 0;
            string name = "";
            byte* c = coffin == Entity.Null ? null : AiRuntime.RO<ServantCoffinstation>(em, coffin, CoffinNeed);
            if (c != null)
            {
                state = *(int*)(c + Layout.ServantCoffinstationStateOffset);
                injury = *(long*)(c + Layout.ServantCoffinstationInjuryEndTicksOffset);
                name = CoffinName(c);
            }
            var outcome = ServantLogic.OutcomeOf(exists, dead, state, injury, now);
            var loot = new List<ServantLoot>();
            var inventory = InventoryRef.None;
            bool haul = false;
            if (outcome != ServantOutcome.Dead)
            {
                int stacks = ItemStacks(s, out inventory, out loot);
                haul = stacks >= 0;
            }
            servants.Add(new ReturnedServant
            {
                Servant = id,
                Coffin = CastleId.From(coffin),
                Name = name,
                Outcome = outcome,
                InjuryEndsUtc = outcome == ServantOutcome.Injured ? ServantLogic.Utc(injury) : null,
                Inventory = inventory,
                Loot = loot,
                LootIsHaul = haul,
            });
        }
        var castle = Progenitor.Castles.Castles.ForHeart(CastleId.From(heart));
        var zones = ZonesFor(em, row.MissionGuid);
        return new ServantsReturned
        {
            Heart = CastleId.From(heart),
            HeartId = castle.Enabled ? castle.HeartId : ContainerId.None,
            Territory = castle.Enabled ? castle.Territory.Index : -1,
            MissionGuid = row.MissionGuid,
            MissionName = PrefabName(world, row.MissionGuid),
            Zone = zones.Count > 0 ? zones[0] : MissionZone.None,
            DataSlot = row.DataSlot,
            LengthSeconds = row.LengthSeconds,
            StartedUtc = ServantLogic.Utc(row.StartTicks),
            ReturnedUtc = ServantLogic.Utc(seen),
            Servants = servants,
            Epoch = WorldEpoch.Current,
        };
    }

    internal static string Describe(ServantsReturned ev)
    {
        var parts = new List<string>();
        foreach (var s in ev.Servants)
            parts.Add((s.Name.Length > 0 ? s.Name : s.Servant.ToString()) + "=" + s.Outcome);
        var loot = new List<string>();
        foreach (var l in ev.TotalLoot) loot.Add(l.ToString());
        return "servants=[" + string.Join(", ", parts) + "] loot=[" + string.Join(", ", loot) + "]";
    }

    // ---- the throne list (experimental) -------------------------------------------------------------

    private static void BeforeInfo(SystemHookContext hook)
    {
        PendingSwaps.Clear();
        var provider = Lists.Provider;
        if (provider == null) return;
        var em = hook.World.EntityManager;
        RefreshCaches(em);
        try
        {
            var requests = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<ServantInfoEvent.Request>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadOnly));
            foreach (var req in requests)
            {
                byte* from = AiRuntime.RO<FromCharacter>(em, req, Layout.FromCharacterCharacterOffset + 8);
                byte* rq = AiRuntime.RO<ServantInfoEvent.Request>(em, req, Layout.ServantInfoEventRequestThroneOffset + 12);
                if (from == null || rq == null) continue;
                var user = AiRuntime.ReadEntity(from + Layout.FromCharacterUserOffset);
                var character = AiRuntime.ReadEntity(from + Layout.FromCharacterCharacterOffset);
                // Copy the throne id now: the provider below may change the world and move the chunk.
                var throne = new byte[12];
                new ReadOnlySpan<byte>(rq + Layout.ServantInfoEventRequestThroneOffset, 12).CopyTo(throne);
                if (user == Entity.Null || !em.Exists(user)) continue;
                int userIndex = em.GetComponentData<User>(user).Index;
                var castle = Progenitor.Castles.Castles.At(CastleId.From(character));
                var request = new ServantListRequest
                {
                    User = CastleId.From(user),
                    Character = CastleId.From(character),
                    Heart = castle.Enabled ? castle.Heart : CastleId.None,
                    Territory = castle.Enabled ? castle.Territory.Index : -1,
                };
                IReadOnlyList<CastleId>? hearts;
                try { hearts = provider.HeartsFor(request); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("servants: list provider '" + Lists.Owner + "' threw; the game's own list is shown: " + e.Message);
                    continue;
                }
                if (hearts == null) continue;
                PendingSwaps[userIndex] = (request.User, request.Heart, throne, new List<CastleId>(hearts));
            }
        }
        catch (Exception e)
        {
            PendingSwaps.Clear();
            Plugin.Logger?.LogWarning("servants: throne list request read failed; the game's own lists are shown: " + e.Message);
        }
    }

    private static void AfterInfo(SystemHookContext hook)
    {
        if (PendingSwaps.Count == 0) return;
        var world = hook.World;
        var em = world.EntityManager;
        try
        {
            var responses = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<ServantInfoEvent.Response>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendEventToUser>(ComponentType.AccessMode.ReadOnly));
            long now = DateTime.UtcNow.Ticks;
            foreach (var resp in responses)
            {
                byte* to = AiRuntime.RO<SendEventToUser>(em, resp, Layout.SendEventToUserUserIndexOffset + 4);
                if (to == null) continue;
                int userIndex = *(int*)(to + Layout.SendEventToUserUserIndexOffset);
                if (!PendingSwaps.TryGetValue(userIndex, out var swap)) continue;
                PendingSwaps.Remove(userIndex);

                var entries = new List<ServantLogic.ThroneEntry>();
                var shown = new ServantListRegistry.Shown { Throne = swap.Throne, User = swap.User, Heart = swap.Heart, AtUtc = DateTime.UtcNow };
                foreach (var heartId in swap.Hearts)
                {
                    var heart = HeartOf(em, heartId.ToEntity());
                    if (heart == Entity.Null) continue;
                    foreach (var coffin in CoffinsOf(em, heart))
                    {
                        if (entries.Count >= ServantLogic.ResponseCapacity) break;
                        var e = EntryFor(em, coffin, heart, now, out var info, world);
                        entries.Add(e);
                        if (!info.Servant.IsNone) shown.Servants.Add((info.Servant, e.NetworkId, CastleId.From(heart)));
                    }
                }
                byte* rw = AiRuntime.RW<ServantInfoEvent.Response>(em, resp, Layout.ServantInfoEventResponseResultOffset + ServantLogic.ResponseListBytes);
                int written = ServantLogic.WriteResponseList(new Span<byte>(rw + Layout.ServantInfoEventResponseResultOffset, ServantLogic.ResponseListBytes), entries);
                Lists.RecordShown(userIndex, shown);
                Plugin.Logger?.LogInfo("servants: throne list swapped for user " + swap.User + " (" + written + " servant(s) of "
                    + swap.Hearts.Count + " heart(s), provider " + Lists.Owner + ")");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: throne list swap failed; the game's own list may have been sent: " + e.Message);
        }
        PendingSwaps.Clear();
    }

    /// <summary>
    /// A throne send from a swapped list that names servants of another heart: the game refuses it
    /// (their coffins are not this throne's), so Progenitor performs it on each servant's own heart
    /// and drops the client event. Sends that name only the throne's own servants are left to the game.
    /// </summary>
    private static void BeforeAction(SystemHookContext hook)
    {
        if (!Lists.AnyShown) return;
        var world = hook.World;
        var em = world.EntityManager;
        RefreshCaches(em); // drops the shown lists of an earlier world load
        if (!Lists.AnyShown) return;
        List<Entity> events;
        try
        {
            events = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<SendOnMissionEvent>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadOnly));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: send event read failed: " + e.Message);
            return;
        }
        var nowUtc = DateTime.UtcNow;
        foreach (var ev in events)
        {
            try { RouteOne(world, em, ev, nowUtc); }
            catch (Exception e) { Plugin.Logger?.LogWarning("servants: routing a throne send failed; left to the game: " + e.Message); }
        }
    }

    private static void RouteOne(World world, EntityManager em, Entity ev, DateTime nowUtc)
    {
        byte* from = AiRuntime.RO<FromCharacter>(em, ev, Layout.FromCharacterCharacterOffset + 8);
        byte* p = AiRuntime.RO<SendOnMissionEvent>(em, ev, Layout.SendOnMissionEventMapZoneIdOffset + 3);
        if (from == null || p == null) return;
        var user = AiRuntime.ReadEntity(from + Layout.FromCharacterUserOffset);
        var character = AiRuntime.ReadEntity(from + Layout.FromCharacterCharacterOffset);
        if (user == Entity.Null || !em.Exists(user)) return;
        int userIndex = em.GetComponentData<User>(user).Index;
        var shown = Lists.ShownTo(userIndex, nowUtc);
        if (shown == null) return;
        if (!ServantLogic.SameNetworkId(new ReadOnlySpan<byte>(p + Layout.SendOnMissionEventThroneOffset, 12), shown.Throne)) return;

        var groups = new Dictionary<CastleId, List<CastleId>>();
        var order = new List<CastleId>();
        bool foreign = false;
        foreach (int off in new[] { Layout.SendOnMissionEventServant1Offset, Layout.SendOnMissionEventServant2Offset, Layout.SendOnMissionEventServant3Offset })
        {
            var id = new ReadOnlySpan<byte>(p + off, 12);
            if (ServantLogic.IsEmptyNetworkId(id)) continue;
            bool known = false;
            foreach (var (servant, net, heart) in shown.Servants)
            {
                if (!ServantLogic.SameNetworkId(id, net)) continue;
                known = true;
                if (heart != shown.Heart) foreign = true;
                if (!groups.TryGetValue(heart, out var list)) { groups[heart] = list = new List<CastleId>(); order.Add(heart); }
                list.Add(servant);
                break;
            }
            if (!known) return; // a servant the swapped list did not show: the game decides
        }
        if (!foreign || order.Count == 0) return;

        int slot = *(int*)(p + Layout.SendOnMissionEventMissionDataIdOffset);
        var zone = ServantLogic.ReadZone(new ReadOnlySpan<byte>(p + Layout.SendOnMissionEventMapZoneIdOffset, 3));
        // The game checks that the sender can reach the throne; the routed send checks that the
        // sender still stands in the throne's castle (the throne itself is not resolved here).
        var here = Progenitor.Castles.Castles.At(CastleId.From(character));
        if (!here.Enabled || here.Heart != shown.Heart) return;
        int mission = MissionFor(em, zone);
        em.DestroyEntity(ev);
        foreach (var heart in order)
        {
            var result = Send(world, new ServantSendRequest
            {
                Heart = heart,
                Servants = groups[heart],
                MissionGuid = mission,
                DataSlot = slot,
                User = CastleId.From(user),
                CheckDiscovered = true,
            }, "progenitor.throne");
            Lists.CountRouted(result.Ok);
            if (!result.Ok)
                Plugin.Logger?.LogInfo("servants: routed throne send for user " + CastleId.From(user) + " refused on heart " + heart + ": " + result.Reason);
        }
    }
}
