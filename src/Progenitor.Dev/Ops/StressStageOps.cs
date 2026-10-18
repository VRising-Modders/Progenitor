using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectM;
using ProjectM.Network;
using Progenitor.Castles;
using Progenitor.Dev.Bots;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using ItemApi = Progenitor.Items.Items;
using ItemGrantPolicy = Progenitor.Items.ItemGrantPolicy;
using StationApi = Progenitor.Stations.Stations;
using CastleApi = Progenitor.Castles.Castles;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Staged stress ops on the castles <c>stress_populate</c>'s offline owners already hold (Moo World copy,
/// isolated test server only, the same gate as <c>stress_populate</c>):
/// <list type="bullet">
/// <item><c>stress_owner_groups {seed, groups, share}</c>: read only. Which owner goes into which clan, the
/// castle-share set and the solo set, with each clan's territories, positions and spread
/// (<see cref="StressGroupPlanner"/>), and the islands with their raw-supply castles.</item>
/// <item><c>stress_clan {seed, groups, clan?, experiment:true}</c>: the server's clan create step for each planned
/// clan, then the game's own add-member routine for every member (<c>TeamUtility.AddUserToClan</c>, a native
/// call through its generated wrapper: explicit opt-in).</item>
/// <item><c>stress_refine {seed, plots, hours, supply}</c>: every refinement recipe on, the way the station
/// window's checkbox does (<see cref="Stations.SetRecipeEnabled"/>), and raw inputs for N hours into chests
/// (<see cref="Progenitor.Items.Items.Grant"/>), spread over the islands' supply castles.</item>
/// <item><c>stress_stations {seed, plots, perCastle}</c>: the top-up plan only. No proven path places a
/// building for an offline owner in an existing castle, so writes are refused.</item>
/// </list>
/// Work runs a few steps per frame under <c>budgetMs</c>; every op has <c>dryRun</c> and a readback on a later
/// frame; <c>stress_stage_status</c> returns the running or last job.
/// </summary>
internal static class StressStageOps
{
    private const int WaitMs = 25_000;
    private static readonly object TickOwner = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StageJob? s_job;
    private static volatile bool s_planning;

    // ---- stress_owner_groups -------------------------------------------------------------------

    public static object OwnerGroups(JsonElement args)
    {
        StressOps.RequireServer("stress_owner_groups");
        var a = StressStageArgs.ParseGroups(args);
        StressOps.RequireGate(null, dryRun: true);
        var world = RequireWorld();
        var collected = Collect(world, a.Seed, null);
        var deferred = new DeferredResult(WaitMs);
        // The hill climb is pure and can take tens of milliseconds: off the main thread.
        Task.Run(() =>
        {
            try
            {
                var g = StressGroupPlanner.Plan(collected.Owners, a.Groups, a.Share, a.Seed, out var refusal);
                if (refusal != null) { deferred.SetException(new InvalidOperationException("owner_groups: " + refusal)); return; }
                var reply = DescribeGrouping(a, collected, g);
                reply["islands"] = DescribeIslands(StressIslands.Build(g, StressSupply.Auto, new StressStageArgs().Suppliers), collected);
                deferred.SetResult(reply);
            }
            catch (Exception e) { deferred.SetException(e); }
        });
        return deferred;
    }

    // ---- stress_clan -----------------------------------------------------------------------------

    public static object Clan(JsonElement args)
    {
        StressOps.RequireServer("stress_clan");
        var a = StressStageArgs.ParseClan(args);
        StressOps.RequireGate(a.Confirm, a.DryRun);
        if (!a.DryRun && !a.Experiment)
            throw new InvalidOperationException("experiment_required: stress_clan calls the game's add-member routine (TeamUtility.AddUserToClan) through its generated wrapper; pass experiment:true to opt in, ideally with clan:1 first");
        var world = RequireIdle();
        var collected = Collect(world, a.Seed, null);
        return PlanThen(world, collected, a, g =>
        {
            var job = new StageJob("clan", a.Seed, a.DryRun, a.BudgetMs, stopOnFailure: true);
            var plan = DescribeGrouping(a, collected, g);
            var notes = new List<string>();
            var em = world.EntityManager;
            var settings = SettingsOrNull(world);
            CastleHeartLimitType limit = CastleHeartLimitType.User;
            try { if (settings != null) limit = settings.CastleStatModifiers_Global.CastleHeartLimitType; }
            catch (Exception e) { notes.Add("CastleHeartLimitType unreadable, using User: " + e.Message); }
            plan["castleHeartLimitType"] = limit.ToString();
            int clanSize = 0;
            try { clanSize = settings?.ClanSize ?? 0; } catch { }
            plan["serverClanSize"] = clanSize;

            bool prefabOk = TryClanPrefab(world, out var prefab, out var prefabWhy);
            plan["clanTeamPrefab"] = prefabOk ? Fmt(prefab) : null;
            if (!prefabOk) notes.Add("no clan prefab: " + prefabWhy);
            var spawnTeams = world.GetExistingSystemManaged<SpawnTeamSystem>();
            if (spawnTeams == null) notes.Add("no SpawnTeamSystem: the clan's TeamValue cannot be taken the way the game takes it");

            var clanRows = new List<object>();
            var made = new List<(StressOwnerGroup Group, Func<Entity> ClanEntity, List<Entity> Members)>();
            foreach (var clan in g.Clans)
            {
                if (a.OnlyClan is int only && clan.Ordinal != only) continue;
                var members = clan.Members.Select(m => collected.UserByOrdinal[m.Ordinal]).ToList();
                string? why = null;
                if (!prefabOk || spawnTeams == null) why = "preflight";
                foreach (var m in clan.Members)
                {
                    var user = collected.UserByOrdinal[m.Ordinal];
                    var seen = GhostOps.ObserveOfflineUserTeam(em, user, out _, out _);
                    if (m.ExistingClan.Length > 0) why ??= m.Name + " is already in clan " + m.ExistingClan;
                    else if (seen.IsConnected) why ??= m.Name + " is connected";
                    else if (OfflineUserTeamRules.Decide(seen).Action != OfflineUserTeamAction.Ready) why ??= m.Name + " has no own team (" + string.Join(", ", OfflineUserTeamRules.Missing(seen)) + ")";
                }
                if (clanSize > 0 && clan.Members.Count > clanSize)
                    notes.Add($"{clan.Name} has {clan.Members.Count} members, above the server's ClanSize {clanSize}; the invite path enforces that limit, AddUserToClan does not");
                clanRows.Add(new Dictionary<string, object?> { ["clan"] = clan.Name, ["members"] = clan.Members.Count, ["refusal"] = why });
                if (why != null || a.DryRun) continue;

                Entity clanEntity = Entity.Null;
                var group = clan;
                job.Add("create " + clan.Name, () =>
                {
                    clanEntity = CreateClan(world, prefab, spawnTeams!, group.Name, members[0], out var detail);
                    return detail;
                });
                for (int i = 0; i < members.Count; i++)
                {
                    var user = members[i];
                    var name = clan.Members[i].Name;
                    int role = i == 0 ? Layout.ClanRoleLeader : Layout.ClanRoleMember;
                    job.Add("join " + clan.Name + " " + name, () => JoinClan(world, clanEntity, user, role, limit));
                }
                made.Add((clan, () => clanEntity, members));
            }
            plan["clanPreflight"] = clanRows;
            plan["notes"] = notes;
            plan["unproven"] = new[]
            {
                "TeamUtility.AddUserToClan through its generated wrapper (ref User argument) on an offline user: source-checked, not live-verified",
                "the clan's TeamData: the create step copies it to members in the same frame; whether SpawnTeamSystem later assigns it has not been read yet (readback compares clan and member TeamData)",
                "ClanRole: written as a component on the user (the game calls ClanSystem_Server.SetClanRole, whose body has not been read yet)",
                "ClanUtility.SetCharacterClanName is skipped: stress owners have no character",
                "ClanMemberStatus is left to the game's clan status system",
                "castle teams are not added to the clan's allies: the game's join path does not do that (each castle team stays allied to its owner's team only, which ClaimCastle asserts)",
            };
            job.Plan = plan;
            job.Readback = () => made.Select(m => ClanReadback(world, m.Group, m.ClanEntity(), m.Members, collected)).ToList<object>();
            return job;
        });
    }

    // ---- stress_refine --------------------------------------------------------------------------

    public static object Refine(JsonElement args)
    {
        StressOps.RequireServer("stress_refine");
        var a = StressStageArgs.ParseRefine(args);
        StressOps.RequireGate(a.Confirm, a.DryRun);
        var world = RequireIdle();
        // The grouping always sees every castle of the seed, so plots:[...] cannot change who is in which island.
        var collected = Collect(world, a.Seed, null);
        return PlanThen(world, collected, a, g =>
        {
            var islands = StressIslands.Build(g, a.Supply, a.Suppliers);
            if (!a.AllPlots)
                islands = StressIslands.Restrict(islands, a.Plots, a.Suppliers);

            var settings = SettingsOrNull(world);
            double rate = 1, cost = 1;
            try { if (settings != null) { rate = settings.RefinementRateModifier; cost = settings.RefinementCostModifier; } } catch { }
            if (!(rate > 0)) rate = 1;
            if (!(cost >= 0)) cost = 1;

            var job = new StageJob("refine", a.Seed, a.DryRun, a.BudgetMs, stopOnFailure: false);
            var held = new HashSet<int>(islands.SelectMany(i => i.Castles).Select(c => c.Territory));
            foreach (int t in a.Plots)
                if (!held.Contains(t)) job.Note($"territory {t} is not held by a seed {a.Seed} stress owner; left alone");
            var reads = new Dictionary<int, CastleRead>();
            var plannedRaws = new Dictionary<int, Dictionary<int, long>>();
            foreach (var island in islands)
                foreach (var c in island.Castles)
                {
                    int t = c.Territory;
                    job.Add("read " + t, () =>
                    {
                        reads[t] = ReadCastle(world, t);
                        return $"{reads[t].Stations.Count} refinement station(s), {reads[t].Chests.Count} chest(s)";
                    });
                }
            var plan = new Dictionary<string, object?>
            {
                ["seed"] = a.Seed,
                ["hours"] = a.Hours,
                ["refinementRate"] = rate,
                ["speed"] = a.Speed,
                ["refinementCost"] = cost,
                ["emptyChests"] = a.EmptyChests,
                ["supply"] = a.Supply.ToString().ToLowerInvariant(),
                ["plots"] = collected.Owners.Sum(o => o.Castles.Count),
            };
            job.Plan = plan;
            job.Add("plan", () =>
            {
                var islandRows = new List<object>();
                int recipeWrites = 0, grantSteps = 0;
                foreach (var island in islands)
                {
                    var islandNeed = new Dictionary<int, long>();
                    var localNeed = new Dictionary<int, Dictionary<int, long>>();
                    foreach (var c in island.Castles)
                    {
                        if (!reads.TryGetValue(c.Territory, out var read)) continue;
                        var need = new Dictionary<int, long>();
                        foreach (var s in read.Stations)
                        {
                            StressRefineMath.Add(need, StressRefineMath.StationNeed(s.Recipes, a.Hours, rate * a.Speed, cost));
                            var off = s.Recipes.Where(r => r.Unlocked && !s.Enabled.Contains(r.Guid)).Select(r => r.Guid).ToList();
                            foreach (int recipe in off)
                            {
                                var station = s.Entity;
                                int rg = recipe;
                                recipeWrites++;
                                if (!a.DryRun)
                                    job.Add($"enable {c.Territory} {Fmt(station)} {rg}", () =>
                                    {
                                        var r = StationApi.SetRecipeEnabled(world, station, rg, true);
                                        if (!r.Ok) throw new InvalidOperationException(r.Reason);
                                        return r.Changed ? $"on ({r.LinkedRowsChanged} linked)" : "already on";
                                    });
                            }
                        }
                        localNeed[c.Territory] = need;
                        StressRefineMath.Add(islandNeed, need);
                    }
                    var perSupplier = island.Supply == StressSupply.Local
                        ? island.Suppliers.Select(t => localNeed.TryGetValue(t, out var n) ? n : new Dictionary<int, long>()).ToList()
                        : StressRefineMath.Split(islandNeed, island.Suppliers.Count);
                    var supplierRows = new List<object>();
                    for (int i = 0; i < island.Suppliers.Count; i++)
                    {
                        int t = island.Suppliers[i];
                        if (!reads.TryGetValue(t, out var read)) continue;
                        var (reserved, stock) = StressChests.Split(read.Chests, a.EmptyChests);
                        var items = perSupplier[i];
                        supplierRows.Add(new Dictionary<string, object?>
                        {
                            ["territory"] = t,
                            ["stockChests"] = stock.Count,
                            ["reservedEmpty"] = reserved.Select(r => r.Entity).ToList(),
                            ["items"] = ItemRows(world, items),
                        });
                        if (stock.Count == 0 && items.Count > 0)
                        {
                            job.Note($"territory {t}: no chest left after keeping {reserved.Count} empty; its raws are skipped");
                            continue;
                        }
                        plannedRaws[t] = items;
                        foreach (var kv in items.OrderBy(k => k.Key))
                        {
                            int item = kv.Key;
                            long amount = kv.Value;
                            grantSteps++;
                            var chests = stock.Select(s => read.ChestEntities[s.Entity]).ToList();
                            if (!a.DryRun)
                                job.Add($"stock {t} {item} x{amount}", () => Stock(world, chests, item, amount));
                        }
                    }
                    islandRows.Add(new Dictionary<string, object?>
                    {
                        ["island"] = island.Id,
                        ["kind"] = island.Kind.ToString().ToLowerInvariant(),
                        ["supply"] = island.Supply.ToString().ToLowerInvariant(),
                        ["territories"] = island.Castles.Select(c => c.Territory).ToList(),
                        ["rawCastles"] = island.Suppliers.ToList(),
                        ["need"] = ItemRows(world, islandNeed),
                        ["suppliers"] = supplierRows,
                    });
                }
                plan["islands"] = islandRows;
                plan["recipeWrites"] = recipeWrites;
                plan["stockSteps"] = grantSteps;
                return $"{islands.Count} island(s), {recipeWrites} recipe switch(es), {grantSteps} stock step(s)";
            });
            job.Readback = () => reads.Keys.OrderBy(t => t).Select(t => RefineReadback(world, t, plannedRaws.TryGetValue(t, out var p) ? p : null)).ToList();
            return job;
        });
    }

    // ---- stress_stations -----------------------------------------------------------------------

    private const string PlacementMissing =
        "no proven path places a station for an offline owner in an existing castle: the build event needs the owner's " +
        "character (CanBuildTileModels, in range, blueprint unlocked by the owner's progression), and the server's castle " +
        "generator (GenerateCastleSystem, proven) only builds a whole castle on an empty territory. Needed: either a " +
        "character for the offline owner that the build event accepts, or a verified equivalent of the generator's " +
        "per-object writes (Instantiate the station prefab, Translation/Rotation, CastleHeartConnection) plus tile " +
        "occupancy so stations do not overlap, then a readback that the station spawned its inventories and has the castle team";

    public static object StationsOp(JsonElement args)
    {
        StressOps.RequireServer("stress_stations");
        var a = StressStageArgs.ParseStations(args);
        StressOps.RequireGate(a.Confirm, a.DryRun);
        var world = RequireIdle();
        var collected = Collect(world, a.Seed, a.AllPlots ? null : a.Plots);
        var job = new StageJob("stations", a.Seed, dryRun: true, a.BudgetMs, stopOnFailure: false);
        var current = new Dictionary<int, List<int>>();
        var territories = collected.Owners.SelectMany(o => o.Castles).Select(c => c.Territory).OrderBy(t => t).ToList();
        foreach (int t0 in territories)
        {
            int t = t0;
            job.Add("read " + t, () =>
            {
                var list = new List<int>();
                foreach (var s in CastleApi.ForTerritory(t).Stations())
                    if (s.IsRefinement) list.Add(s.Building.PrefabGuid);
                current[t] = list;
                return list.Count + " refinement station(s)";
            });
        }
        var plan = new Dictionary<string, object?> { ["seed"] = a.Seed, ["perCastle"] = a.PerCastle, ["writes"] = "refused: " + PlacementMissing };
        job.Plan = plan;
        job.Add("plan", () =>
        {
            var catalog = current.Values.SelectMany(v => v).GroupBy(g => g).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Select(x => x.Key).ToList();
            var rows = new List<object>();
            int total = 0;
            foreach (var kv in current.OrderBy(k => k.Key))
            {
                var add = StressStationPlanner.Plan(kv.Value, a.PerCastle, catalog);
                total += add.Count;
                rows.Add(new Dictionary<string, object?>
                {
                    ["territory"] = kv.Key,
                    ["refinementStations"] = kv.Value.Count,
                    ["add"] = add.GroupBy(x => x).Select(x => new Dictionary<string, object?> { ["prefab"] = Name(world, x.Key), ["guid"] = x.Key, ["count"] = x.Count() }).ToList(),
                });
            }
            plan["catalog"] = catalog.Select(c => Name(world, c)).ToList();
            plan["castles"] = rows;
            plan["stationsToAdd"] = total;
            plan["missing"] = PlacementMissing;
            return total + " station(s) would be added";
        });
        if (!a.DryRun) job.Note("not a dry run, but stations cannot be placed yet: plan only, nothing written");
        job.Readback = () => current.OrderBy(k => k.Key).Select(kv => (object)new Dictionary<string, object?> { ["territory"] = kv.Key, ["refinementStations"] = kv.Value.Count }).ToList();
        return Start(world, job);
    }

    // ---- status / shutdown ---------------------------------------------------------------------

    public static object Status(JsonElement args)
    {
        var job = s_job;
        if (job == null) return new Dictionary<string, object?> { ["phase"] = "idle" };
        return job.Describe();
    }

    internal static void Shutdown()
    {
        var job = s_job;
        if (job == null) return;
        job.Cancel("Dev unloaded");
        try { job.Pending?.SetException(new InvalidOperationException("Dev unloaded while the stress stage job ran")); } catch { }
        job.Pending = null;
    }

    // ---- live steps ----------------------------------------------------------------------------

    private static unsafe Entity CreateClan(World world, Entity prefab, SpawnTeamSystem spawnTeams, string name, Entity leader, out string detail)
    {
        var em = world.EntityManager;
        var clan = em.Instantiate(prefab);
        try
        {
            var type = ComponentIndex.Of<ClanTeam>(ComponentType.AccessMode.ReadWrite);
            byte* p = (byte*)em.GetComponentDataRawRW(clan, type.TypeIndex);
            if (p == null) throw new InvalidOperationException("ClanTeam GetComponentDataRawRW returned null");
            PutFixedString64(p + Layout.ClanTeamNameOffset, name);
            PutFixedString64(p + Layout.ClanTeamMottoOffset, "Progenitor stress test");
            // A random version-4 GUID, the bits the game sets.
            var guid = RandomNumberGenerator.GetBytes(16);
            guid[7] = (byte)((guid[7] & 0x0F) | 0x40);
            guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
            for (int i = 0; i < 16; i++) p[Layout.ClanTeamClanGuidOffset + i] = guid[i];
            int teamValue = spawnTeams.LastCreatedTeamId + 1;
            spawnTeams.LastCreatedTeamId = teamValue;
            *(int*)(p + Layout.ClanTeamTeamValueOffset) = teamValue;
            var by = new byte[Layout.NetworkIdSize];
            var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(leader, netType))
            {
                byte* src = (byte*)em.GetComponentDataRawRO(leader, netType.TypeIndex);
                if (src != null) for (int i = 0; i < by.Length; i++) by[i] = src[i];
            }
            for (int i = 0; i < by.Length; i++)
            {
                p[Layout.ClanTeamCreatedByOffset + i] = by[i];
                p[Layout.ClanTeamLastEditedByOffset + i] = by[i];
            }
            detail = $"clan {Fmt(clan)} teamValue {teamValue}";
            Plugin.Logger?.LogInfo($"stress clan created {name} entity {Fmt(clan)} teamValue {teamValue}");
            return clan;
        }
        catch
        {
            try { if (em.Exists(clan)) em.DestroyEntity(clan); } catch { }
            throw;
        }
    }

    private static unsafe string JoinClan(World world, Entity clan, Entity user, int role, CastleHeartLimitType limit)
    {
        var em = world.EntityManager;
        if (clan == Entity.Null || !em.Exists(clan)) throw new InvalidOperationException("the clan entity is gone");
        var u = em.GetComponentData<User>(user);
        if (u.IsConnected) throw new InvalidOperationException("the owner is connected");
        if (u.ClanEntity._Entity != Entity.Null) throw new InvalidOperationException("the owner is already in clan " + Fmt(u.ClanEntity._Entity));
        TeamUtility.AddUserToClan(em, clan, user, ref u, limit);
        if (u.ClanEntity._Entity != clan) throw new InvalidOperationException("AddUserToClan did not set ClanEntity on the copy");

        // The game writes its copy of the User back; only ClanEntity changed, so only that field is written.
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadWrite);
        byte* p = (byte*)em.GetComponentDataRawRW(user, userType.TypeIndex);
        if (p == null) throw new InvalidOperationException("User GetComponentDataRawRW returned null");
        *(int*)(p + Layout.UserClanEntityOffset) = clan.Index;
        *(int*)(p + Layout.UserClanEntityOffset + 4) = clan.Version;
        *(int*)(p + Layout.UserClanEntityOffset + 8) = 0;

        if (!user.Has<ClanRole>(em)) user.Add<ClanRole>(em);
        var roleType = ComponentIndex.Of<ClanRole>(ComponentType.AccessMode.ReadWrite);
        int* r = (int*)em.GetComponentDataRawRW(user, roleType.TypeIndex);
        if (r == null) throw new InvalidOperationException("ClanRole GetComponentDataRawRW returned null");
        *(int*)((byte*)r + Layout.ClanRoleValueOffset) = role;
        Plugin.Logger?.LogInfo($"stress clan join user {Fmt(user)} clan {Fmt(clan)} role {role}");
        return $"user {Fmt(user)} role {role}";
    }

    private static string Stock(World world, List<Entity> chests, int item, long amount)
    {
        long left = amount;
        var guid = new PrefabGUID(item);
        int used = 0;
        foreach (var chest in chests)
        {
            if (left <= 0) break;
            bool any = false;
            while (left > 0)
            {
                int chunk = (int)Math.Min(left, ItemGrantPolicy.MaxAmount);
                var r = ItemApi.Grant(world, chest, guid, chunk);
                if (r.Added > 0) { left -= r.Added; any = true; }
                if (!r.Ok || r.Added < chunk) break;
            }
            if (any) used++;
        }
        if (left > 0) throw new InvalidOperationException($"{amount - left} of {amount} stocked in {used} chest(s); {left} did not fit");
        return $"{amount} in {used} chest(s)";
    }

    // ---- reads ---------------------------------------------------------------------------------

    private sealed class Collected
    {
        public List<StressOwnerSpots> Owners { get; } = new();
        public Dictionary<int, Entity> UserByOrdinal { get; } = new();
        public Dictionary<int, CastlePlot> Plots { get; } = new();
        public List<string> Notes { get; } = new();
    }

    /// <summary>This seed's owners and the castles they hold; <paramref name="only"/> limits the castles.</summary>
    private static Collected Collect(World world, int seed, IReadOnlyList<int>? only)
    {
        var em = world.EntityManager;
        var c = new Collected();
        var byPlatform = new Dictionary<ulong, StressOwnerSpots>();
        foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
        {
            User u;
            try { u = em.GetComponentData<User>(e); } catch { continue; }
            if (!StressIdentity.TrySeedOf(u.PlatformId, out int s, out int ordinal) || s != seed) continue;
            string name;
            try { name = u.CharacterName.ToString(); } catch { name = StressIdentity.OwnerName(ordinal); }
            var clan = u.ClanEntity._Entity;
            var o = new StressOwnerSpots
            {
                Ordinal = ordinal,
                Name = name,
                PlatformId = u.PlatformId,
                UserIndex = u.Index,
                ExistingClan = clan != Entity.Null && em.Exists(clan) ? Fmt(clan) : "",
            };
            byPlatform[u.PlatformId] = o;
            c.UserByOrdinal[ordinal] = e;
        }
        var wanted = only == null ? null : new HashSet<int>(only);
        foreach (var plot in CastleReader.Plots(world))
        {
            if (!byPlatform.TryGetValue(plot.OwnerPlatformId, out var owner)) continue;
            if (wanted != null && !wanted.Contains(plot.TerritoryIndex)) continue;
            int cx = (plot.BoundsMinX + plot.BoundsMaxX) / 2, cy = (plot.BoundsMinY + plot.BoundsMaxY) / 2;
            owner.Castles.Add(new StressCastleSpot
            {
                Territory = plot.TerritoryIndex,
                X = Math.Round(CastleGeometry.TileToWorld(cx), 1),
                Z = Math.Round(CastleGeometry.TileToWorld(cy), 1),
                Region = BotTerritories.RegionOf(em, plot.TerritoryEntity),
                Owner = owner.Ordinal,
            });
            c.Plots[plot.TerritoryIndex] = plot;
        }
        if (wanted != null)
            foreach (int t in wanted)
                if (!c.Plots.ContainsKey(t)) c.Notes.Add($"territory {t} is not held by a seed {seed} stress owner; left out");
        foreach (var o in byPlatform.Values.OrderBy(o => o.Ordinal))
        {
            o.Castles.Sort((x, y) => x.Territory.CompareTo(y.Territory));
            if (o.Castles.Count > 0) c.Owners.Add(o);
        }
        return c;
    }

    private sealed class StationRead
    {
        public Entity Entity;
        public List<StressRecipe> Recipes { get; } = new();
        public HashSet<int> Enabled { get; } = new();
    }

    private sealed class CastleRead
    {
        public List<StationRead> Stations { get; } = new();
        public List<StressChest> Chests { get; } = new();
        public Dictionary<string, Entity> ChestEntities { get; } = new(StringComparer.Ordinal);
    }
    private static CastleRead ReadCastle(World world, int territory)
    {
        var em = world.EntityManager;
        var read = new CastleRead();
        var castle = CastleApi.ForTerritory(territory);
        foreach (var s in castle.Stations())
        {
            if (!s.IsRefinement) continue;
            var e = new Entity { Index = s.Entity.Index, Version = s.Entity.Version };
            var sr = new StationRead { Entity = e };
            foreach (var r in StationApi.Recipes(world, e))
            {
                var rec = new StressRecipe { Guid = r.RecipeGuid, Name = r.Name, DurationSeconds = r.DurationSeconds, Unlocked = r.Unlocked };
                foreach (var i in r.Inputs) rec.Inputs.Add((i.ItemGuid, i.Amount));
                sr.Recipes.Add(rec);
                if (r.Enabled) sr.Enabled.Add(r.RecipeGuid);
            }
            read.Stations.Add(sr);
        }
        int order = 0;
        foreach (var ch in castle.Containers())
        {
            if (ch.IsStation) continue;
            var e = new Entity { Index = ch.Entity.Index, Version = ch.Entity.Version };
            if (e.Has<ProjectM.CastleBuilding.CastleHeart>(em)) continue;
            UsedSlots(em, e, out int slots, out int used);
            read.Chests.Add(new StressChest { Entity = Fmt(e), Order = order++, Slots = slots, UsedSlots = used });
            read.ChestEntities[Fmt(e)] = e;
        }
        return read;
    }

    /// <summary>Recipe switches and, for the planned raws, what the castle's chests hold now.</summary>
    private static object RefineReadback(World world, int territory, IReadOnlyDictionary<int, long>? planned)
    {
        var em = world.EntityManager;
        var read = ReadCastle(world, territory);
        int unlocked = read.Stations.Sum(s => s.Recipes.Count(r => r.Unlocked));
        int on = read.Stations.Sum(s => s.Recipes.Count(r => r.Unlocked && s.Enabled.Contains(r.Guid)));
        int locked = read.Stations.Sum(s => s.Recipes.Count(r => !r.Unlocked));
        var row = new Dictionary<string, object?>
        {
            ["territory"] = territory,
            ["stations"] = read.Stations.Count,
            ["recipesOn"] = on,
            ["recipesUnlocked"] = unlocked,
            ["recipesLocked"] = locked,
            ["allUnlockedOn"] = on == unlocked,
            ["chests"] = read.Chests.Count,
            ["emptyChests"] = read.Chests.Count(c => c.Empty),
        };
        if (planned != null && planned.Count > 0)
        {
            var held = new Dictionary<int, long>();
            foreach (var e in read.ChestEntities.Values)
                foreach (var kv in Totals(em, e))
                    held[kv.Key] = (held.TryGetValue(kv.Key, out long n) ? n : 0) + kv.Value;
            row["raws"] = planned.OrderBy(k => k.Key).Select(kv => (object)new Dictionary<string, object?>
            {
                ["item"] = Name(world, kv.Key),
                ["planned"] = kv.Value,
                ["inChests"] = held.TryGetValue(kv.Key, out long h) ? h : 0,
            }).ToList();
        }
        return row;
    }

    private static Dictionary<int, long> Totals(EntityManager em, Entity owner)
    {
        var totals = new Dictionary<int, long>();
        var inv = InventoryOf(em, owner);
        if (inv == Entity.Null) return totals;
        var buffer = em.GetBuffer<InventoryBuffer>(inv, true);
        for (int i = 0; i < buffer.Length; i++)
        {
            var row = buffer[i];
            if (row.ItemType.GuidHash == 0 || row.Amount <= 0) continue;
            totals[row.ItemType.GuidHash] = (totals.TryGetValue(row.ItemType.GuidHash, out long n) ? n : 0) + row.Amount;
        }
        return totals;
    }

    private static object ClanReadback(World world, StressOwnerGroup group, Entity clan, List<Entity> members, Collected collected)
    {
        var em = world.EntityManager;
        var row = new Dictionary<string, object?> { ["clan"] = group.Name, ["entity"] = clan == Entity.Null ? null : Fmt(clan) };
        if (clan == Entity.Null || !em.Exists(clan)) { row["exists"] = false; return row; }
        row["exists"] = true;
        var clanAllies = new List<Entity>();
        GhostOps.TryReadAllies(em, clan, clanAllies);
        row["allies"] = clanAllies.ConvertAll(Fmt);
        int clanTeamData = ReadInt<TeamData>(em, clan, Layout.TeamDataTeamValueOffset);
        row["teamData"] = clanTeamData;
        row["clanTeamValue"] = ReadInt<ClanTeam>(em, clan, Layout.ClanTeamTeamValueOffset);
        var rows = new List<object>();
        bool allOk = true;
        for (int i = 0; i < members.Count; i++)
        {
            var user = members[i];
            var m = group.Members[i];
            var u = em.GetComponentData<User>(user);
            GhostOps.ObserveOfflineUserTeam(em, user, out var team, out var allies);
            int memberTeamData = ReadInt<TeamData>(em, team, Layout.TeamDataTeamValueOffset);
            bool inClan = u.ClanEntity._Entity == clan;
            bool linked = allies.Contains(clan) && clanAllies.Contains(team);
            var castleRows = new List<object>();
            foreach (var spot in m.Castles)
            {
                if (!collected.Plots.TryGetValue(spot.Territory, out var plot)) continue;
                GhostOps.ObserveClaim(em, user, plot.HeartEntity, out var castleTeam, out var castleAllies);
                castleRows.Add(new Dictionary<string, object?>
                {
                    ["territory"] = spot.Territory,
                    ["castleTeam"] = Fmt(castleTeam),
                    ["castleAllies"] = castleAllies.ConvertAll(Fmt),
                    ["alliedToOwner"] = castleAllies.Count == 1 && castleAllies[0] == team,
                    ["castleTeamInClanAllies"] = clanAllies.Contains(castleTeam),
                });
            }
            bool ok = inClan && linked && memberTeamData == clanTeamData;
            allOk &= ok;
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["user"] = Fmt(user),
                ["clanEntity"] = Fmt(u.ClanEntity._Entity),
                ["team"] = Fmt(team),
                ["teamAllies"] = allies.ConvertAll(Fmt),
                ["teamData"] = memberTeamData,
                ["role"] = user.Has<ClanRole>(em) ? ReadInt<ClanRole>(em, user, Layout.ClanRoleValueOffset) : (int?)null,
                ["ok"] = ok,
                ["castles"] = castleRows,
            });
        }
        row["members"] = rows;
        row["ok"] = allOk;
        return row;
    }

    private static unsafe int ReadInt<T>(EntityManager em, Entity e, int offset)
    {
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
        if (e == Entity.Null || !em.Exists(e) || !em.HasComponent(e, type)) return int.MinValue;
        byte* p = (byte*)em.GetComponentDataRawRO(e, type.TypeIndex);
        return p == null ? int.MinValue : *(int*)(p + offset);
    }

    /// <summary>Slots and used slots of the owner's first inventory (its own buffer or instance 0), through the typed accessor bot_station uses.</summary>
    private static unsafe void UsedSlots(EntityManager em, Entity owner, out int slots, out int used)
    {
        slots = used = 0;
        var inv = InventoryOf(em, owner);
        if (inv == Entity.Null) return;
        var buffer = em.GetBuffer<InventoryBuffer>(inv, true);
        slots = buffer.Length;
        for (int i = 0; i < buffer.Length; i++)
            if (buffer[i].ItemType.GuidHash != 0 && buffer[i].Amount > 0) used++;
    }

    private static unsafe Entity InventoryOf(EntityManager em, Entity owner)
    {
        var invType = ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadOnly);
        if (em.HasComponent(owner, invType)) return owner;
        var instType = ComponentIndex.Of<InventoryInstanceElement>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(owner, instType) || em.GetBufferLength(owner, instType.TypeIndex) < 1) return Entity.Null;
        byte* raw = (byte*)em.GetBufferRawRO(owner, instType.TypeIndex);
        if (raw == null) return Entity.Null;
        var bag = new Entity { Index = *(int*)(raw + Layout.InventoryInstanceElementExternalInventoryEntityOffset), Version = *(int*)(raw + Layout.InventoryInstanceElementExternalInventoryEntityOffset + 4) };
        return bag != Entity.Null && em.Exists(bag) && em.HasComponent(bag, invType) ? bag : Entity.Null;
    }

    private static bool TryClanPrefab(World world, out Entity prefab, out string why)
    {
        prefab = Entity.Null;
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (prefabs == null) { why = "no PrefabCollectionSystem"; return false; }
        int guid;
        try { guid = prefabs._RootPrefabs.ClanTeamPrefabGUID.GuidHash; }
        catch (Exception e) { why = "RootPrefabCollection read failed: " + e.Message; return false; }
        if (guid == 0) { why = "RootPrefabCollection.ClanTeamPrefabGUID is 0"; return false; }
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(guid), out prefab) || prefab == Entity.Null || !em.Exists(prefab))
        {
            prefab = Entity.Null;
            why = $"clan team prefab {guid} is not in _PrefabGuidToEntityMap";
            return false;
        }
        foreach (var (type, name) in new[] { (ComponentIndex.Of<ClanTeam>(ComponentType.AccessMode.ReadOnly), "ClanTeam"), (ComponentIndex.Of<TeamAllies>(ComponentType.AccessMode.ReadOnly), "TeamAllies"), (ComponentIndex.Of<TeamData>(ComponentType.AccessMode.ReadOnly), "TeamData") })
        {
            if (!em.HasComponent(prefab, type)) { why = $"clan team prefab {guid} lacks {name}"; prefab = Entity.Null; return false; }
        }
        why = "";
        return true;
    }

    private static ServerGameSettings? SettingsOrNull(World world)
    {
        try { return world.GetExistingSystemManaged<ServerGameSettingsSystem>()?.Settings; }
        catch { return null; }
    }

    // ---- describe ------------------------------------------------------------------------------

    private static Dictionary<string, object?> DescribeGrouping(StressStageArgs a, Collected c, StressGrouping g)
    {
        object Owner(StressOwnerSpots o) => new Dictionary<string, object?>
        {
            ["name"] = o.Name,
            ["platformId"] = o.PlatformId.ToString(CultureInfo.InvariantCulture),
            ["userIndex"] = o.UserIndex,
            ["castles"] = o.Castles.Count,
            ["ownSpread"] = Math.Round(StressGroupPlanner.OwnSpread(o), 1),
            ["territories"] = o.Castles.Select(s => s.Territory).ToList(),
        };
        object Spread(StressSpreadMetrics m) => new Dictionary<string, object?>
        {
            ["castles"] = m.Castles,
            ["regions"] = m.Regions,
            ["meanPairDistance"] = Math.Round(m.MeanPairDistance, 1),
            ["minPairDistance"] = Math.Round(m.MinPairDistance, 1),
            ["meanNearestDistance"] = Math.Round(m.MeanNearestDistance, 1),
            ["score"] = Math.Round(m.Score, 4),
        };
        object Group(StressOwnerGroup grp) => new Dictionary<string, object?>
        {
            ["name"] = grp.Name,
            ["ordinal"] = grp.Ordinal,
            ["members"] = grp.Members.Select(Owner).ToList(),
            ["platformIds"] = grp.Members.Select(m => m.PlatformId.ToString(CultureInfo.InvariantCulture)).ToList(),
            ["castlesPerOwner"] = grp.Members.GroupBy(m => m.Castles.Count).OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString(CultureInfo.InvariantCulture), x => (object?)x.Count()),
            ["positions"] = grp.Castles.OrderBy(s => s.Territory).Select(s => new Dictionary<string, object?> { ["territory"] = s.Territory, ["x"] = s.X, ["z"] = s.Z, ["region"] = s.Region, ["owner"] = StressIdentity.OwnerName(s.Owner) }).ToList(),
            ["spread"] = Spread(grp.Spread),
        };
        return new Dictionary<string, object?>
        {
            ["seed"] = a.Seed,
            ["owners"] = c.Owners.Count,
            ["castles"] = c.Owners.Sum(o => o.Castles.Count),
            ["groups"] = a.Groups,
            ["clans"] = g.Clans.Select(Group).ToList(),
            ["share"] = Group(g.Share),
            ["solo"] = Group(g.Solo),
            ["mixing"] = Math.Round(g.Mixing, 4),
            ["score"] = Math.Round(g.Score, 4),
            ["mapDiagonal"] = Math.Round(g.Diagonal, 1),
            ["regions"] = g.RegionsTotal,
            ["swaps"] = g.Swaps,
            ["notes"] = g.Notes.Concat(c.Notes).ToList(),
            ["rule"] = "solo = most compact owners; clans and castle-share by a hill climb over swaps maximizing 0.55 clan spread + 0.35 neighbor mixing + 0.10 castle-share own spread; owners keep their castles",
        };
    }

    private static List<object> DescribeIslands(List<StressIsland> islands, Collected c) =>
        islands.Select(i => (object)new Dictionary<string, object?>
        {
            ["island"] = i.Id,
            ["kind"] = i.Kind.ToString().ToLowerInvariant(),
            ["supply"] = i.Supply.ToString().ToLowerInvariant(),
            ["territories"] = i.Castles.Select(x => x.Territory).ToList(),
            ["rawCastles"] = i.Suppliers.ToList(),
        }).ToList();

    private static List<object> ItemRows(World world, IReadOnlyDictionary<int, long> items) =>
        items.OrderBy(k => k.Key).Select(kv => (object)new Dictionary<string, object?> { ["item"] = Name(world, kv.Key), ["guid"] = kv.Key, ["amount"] = kv.Value }).ToList();

    private static string Name(World world, int guid) =>
        PrefabLookup.TryGetName(world, new PrefabGUID(guid), out var n) && !string.IsNullOrEmpty(n) ? n : guid.ToString(CultureInfo.InvariantCulture);

    private static unsafe void PutFixedString64(byte* dest, string text)
    {
        var fs = new FixedString64Bytes(text ?? "");
        byte* src = (byte*)&fs;
        for (int i = 0; i < 64; i++) dest[i] = src[i];
    }

    private static string Fmt(Entity e) => $"{e.Index}:{e.Version}";

    private static World RequireWorld()
    {
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("no server world; wait until ping reports a Server world");
        return world;
    }

    private static World RequireIdle()
    {
        if (s_planning) throw new InvalidOperationException("a stress stage job is still planning; read stress_stage_status in a moment");
        var running = s_job;
        if (running != null && !running.Finished)
            throw new InvalidOperationException($"a stress stage {running.Kind} job is still running ({running.Phase}); read stress_stage_status");
        return StressOps.RequireIdle();
    }

    // ---- runner --------------------------------------------------------------------------------

    /// <summary>
    /// The grouping's hill climb is pure and takes about 0.1 s for 40 owners, so it runs off the main
    /// thread; the job is then built and started back on the main thread.
    /// </summary>
    private static object PlanThen(World world, Collected collected, StressStageArgs a, Func<StressGrouping, StageJob> build)
    {
        var deferred = new DeferredResult(WaitMs);
        s_planning = true;
        Task.Run(() =>
        {
            StressGrouping? g = null;
            string? refusal = null;
            Exception? error = null;
            try { g = StressGroupPlanner.Plan(collected.Owners, a.Groups, a.Share, a.Seed, out refusal); }
            catch (Exception e) { error = e; }
            MainThread.Post(() =>
            {
                s_planning = false;
                try
                {
                    if (error != null) throw new InvalidOperationException("owner_groups threw: " + error.Message, error);
                    if (refusal != null) throw new InvalidOperationException("owner_groups: " + refusal);
                    if (!world.IsCreated || !ReferenceEquals(world, Progenitor.Worlds.Server)) throw new InvalidOperationException("the server world changed while planning");
                    Start(world, build(g!), deferred);
                }
                catch (Exception e) { deferred.SetException(e); }
            }, TickOwner);
        });
        return deferred;
    }

    private static object Start(World world, StageJob job) => Start(world, job, new DeferredResult(WaitMs));

    private static object Start(World world, StageJob job, DeferredResult deferred)
    {
        s_job = job;
        job.World = world;
        job.Pending = deferred;
        Plugin.Logger?.LogWarning($"stress_{job.Kind}: seed {job.Seed} dryRun {job.DryRun} steps {job.StepCount}");
        MainThread.PostDelayed(0, Tick, TickOwner);
        return deferred;
    }

    private static void Tick()
    {
        var job = s_job;
        if (job == null || job.Finished) return;
        var world = job.World;
        if (world == null || !world.IsCreated || !ReferenceEquals(world, Progenitor.Worlds.Server))
            job.Cancel("the server world changed");
        else
        {
            try { job.Tick(() => Clock.Elapsed.TotalMilliseconds); }
            catch (Exception e) { job.Cancel("tick threw: " + e.Message); }
        }
        if (job.StepsDone && !job.Finished && !job.ReadbackScheduled)
        {
            job.ReadbackScheduled = true;
            MainThread.PostDelayed(job.DryRun ? 0 : 1.0, () =>
            {
                try { job.ReadbackRows = job.Readback?.Invoke(); }
                catch (Exception e) { job.Note("readback threw: " + e.Message); }
                job.Finish();
                Plugin.Logger?.LogInfo($"stress_{job.Kind} seed {job.Seed}: {job.Phase} {job.Reason} ({job.Done}/{job.StepCount} steps, {job.Failed} failed, max tick {job.MaxTickMs:0.0} ms)");
                Resolve(job);
            }, TickOwner);
            return;
        }
        if (job.Finished) { Resolve(job); return; }
        MainThread.PostDelayed(0, Tick, TickOwner);
    }

    private static void Resolve(StageJob job)
    {
        var pending = job.Pending;
        job.Pending = null;
        if (pending == null) return;
        try { pending.SetResult(job.Describe()); } catch (Exception e) { pending.SetException(e); }
    }

    private sealed class StageJob
    {
        private readonly List<(string Label, Func<string> Run)> _steps = new();
        private readonly List<Dictionary<string, object?>> _rows = new();
        private readonly List<string> _notes = new();
        private readonly bool _stopOnFailure;
        private readonly double _budgetMs;
        private int _at;

        public StageJob(string kind, int seed, bool dryRun, double budgetMs, bool stopOnFailure)
        {
            Kind = kind;
            Seed = seed;
            DryRun = dryRun;
            _budgetMs = budgetMs;
            _stopOnFailure = stopOnFailure;
        }

        public string Kind { get; }
        public int Seed { get; }
        public bool DryRun { get; }
        public World? World { get; set; }
        public DeferredResult? Pending { get; set; }
        public Dictionary<string, object?> Plan { get; set; } = new();
        public Func<List<object>>? Readback { get; set; }
        public List<object>? ReadbackRows { get; set; }
        public bool ReadbackScheduled { get; set; }
        public string Phase { get; private set; } = "running";
        public string Reason { get; private set; } = "";
        public bool Finished { get; private set; }
        public bool StepsDone => _at >= _steps.Count;
        public int StepCount => _steps.Count;
        public int Done { get; private set; }
        public int Failed { get; private set; }
        public int Ticks { get; private set; }
        public double MaxTickMs { get; private set; }

        /// <summary>Queue a step. A dry run still runs reads and the plan step; write steps are only queued when not dry.</summary>
        public void Add(string label, Func<string> run) => _steps.Add((label, run));

        public void Note(string text) => _notes.Add(text);

        public void Tick(Func<double> nowMs)
        {
            double start = nowMs();
            Ticks++;
            while (_at < _steps.Count && !Finished)
            {
                var (label, run) = _steps[_at++];
                var row = new Dictionary<string, object?> { ["step"] = label };
                try
                {
                    row["ok"] = true;
                    row["detail"] = run();
                    Done++;
                }
                catch (Exception e)
                {
                    row["ok"] = false;
                    row["detail"] = e.Message;
                    Failed++;
                    Plugin.Logger?.LogWarning($"stress_{Kind} step '{label}' failed: {e.Message}");
                    if (_stopOnFailure) { _rows.Add(row); Stop("stopped", label + ": " + e.Message); break; }
                }
                _rows.Add(row);
                if (nowMs() - start >= _budgetMs) break;
            }
            MaxTickMs = Math.Max(MaxTickMs, nowMs() - start);
        }

        private void Stop(string phase, string reason)
        {
            Phase = phase;
            Reason = reason;
            _at = _steps.Count;
        }

        public void Cancel(string reason)
        {
            if (Finished) return;
            Stop("cancelled", reason);
            Finished = true;
        }

        public void Finish()
        {
            if (Finished) return;
            if (Phase == "running") Phase = DryRun ? "planned" : Failed > 0 ? "done_with_failures" : "done";
            Finished = true;
        }

        public Dictionary<string, object?> Describe() => new()
        {
            ["op"] = "stress_" + Kind,
            ["seed"] = Seed,
            ["dryRun"] = DryRun,
            ["phase"] = Phase,
            ["reason"] = Reason,
            ["finished"] = Finished,
            ["steps"] = _steps.Count,
            ["done"] = Done,
            ["failed"] = Failed,
            ["ticks"] = Ticks,
            ["maxTickMs"] = Math.Round(MaxTickMs, 2),
            ["plan"] = Plan,
            ["notes"] = _notes.ToList(),
            ["rows"] = _rows.Count > 400 ? _rows.Where(r => r["ok"] is false).Take(200).Concat(_rows.Skip(_rows.Count - 50)).ToList() : _rows.ToList(),
            ["readback"] = ReadbackRows,
        };
    }
}
