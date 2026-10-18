using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BepInEx;
using ProjectM.Network;
using Progenitor.Castles;
using Progenitor.Dev.Bots;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server ops <c>stress_populate</c>, <c>stress_clear</c> and <c>stress_status</c>: fill the free
/// castle plots of the Moo World copy test save with test castles owned by deterministic offline
/// test owners (Stress01...), and remove them again. Approved by the user for that save on the
/// isolated test server only (<see cref="StressGate"/>); owners persist in that save until
/// <c>stress_clear</c>, an exception to the same-batch rule for ghost users that covers this op only.
///
/// <para>The plan (<see cref="StressPlanner"/>) and the frame-budgeted runner (<see cref="StressJob"/>)
/// are game-free. This class is the live side. Each step runs only through a proven path; a step
/// kind with none is refused (whole op, or per plot with <c>partial:true</c>) and listed under
/// <c>missing</c>. In this build only the owner step has one: the live-verified <c>ghost</c> User
/// write (<see cref="GhostOps.CreateOfflineUser"/>), with <c>IsConnected</c> false, followed by the owner's
/// own team (<see cref="GhostOps.EnsureOfflineUserTeam"/>, the server's new-user team path; source-checked,
/// not live-verified). <c>stress_team</c> reads those teams back. Discovery uses the
/// <c>bot_territories</c> reads. No native call, no new generic interop instantiation.</para>
/// </summary>
internal static class StressOps
{
    private const int PopulateWaitMs = 20_000;
    private const int ClearWaitMs = 10_000;
    private static readonly object TickOwner = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StressJob? s_job;
    private static World? s_world;
    private static DeferredResult? s_pending;
    private static bool s_pendingNeedsFinish;

    public static object Populate(JsonElement args)
    {
        RequireServer("stress_populate");
        var a = StressArgs.ParsePopulate(args);
        RequireGate(a.Confirm, a.DryRun);
        var world = RequireIdle();
        var job = StressJob.Populate(a, new LiveBackend(world));
        Plugin.Logger?.LogWarning(
            $"stress_populate: seed {a.Seed} owners {a.Owners} clans {a.Clans} plots {(a.AllFree ? "free" : string.Join(",", a.Plots))} " +
            $"chests {a.ChestsMin}-{a.ChestsMax} stations {a.StationsMin}-{a.StationsMax} fill {a.Fill.ToString(CultureInfo.InvariantCulture)} " +
            $"budget {a.BudgetMs.ToString(CultureInfo.InvariantCulture)} ms dryRun {a.DryRun} partial {a.Partial}");
        return Start(world, job, PopulateWaitMs, needsFinish: false);
    }

    public static object Clear(JsonElement args)
    {
        RequireServer("stress_clear");
        var a = StressArgs.ParseClear(args);
        RequireGate(a.Confirm, a.DryRun);
        var world = RequireIdle();
        var job = StressJob.Clear(a, new LiveBackend(world));
        Plugin.Logger?.LogWarning($"stress_clear: seed {a.Seed} dryRun {a.DryRun}");
        return Start(world, job, ClearWaitMs, needsFinish: true);
    }

    /// <summary>The running or last job. Args: <c>plot</c> (territory index) for its full layout; <c>rows:false</c> for counts only.</summary>
    public static object Status(JsonElement args)
    {
        var job = s_job;
        if (job == null) return new Dictionary<string, object?> { ["phase"] = "idle", ["running"] = false };
        int? plot = null;
        bool rows = true;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("plot", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n)) plot = n;
            if (args.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.False) rows = false;
        }
        return job.Describe(plot, rows);
    }

    /// <summary>
    /// Read only. For each owner of <c>seed</c>: its team (<c>TeamReference</c>), that team's
    /// <c>UserTeam</c> back-pointer and <c>TeamAllies</c>. With <c>heart</c> (<c>"index:version"</c>):
    /// that heart's castle team and allies, and per owner whether every read
    /// <c>TeamUtility.ClaimCastle</c> makes would succeed. Never writes, never calls ClaimCastle.
    /// </summary>
    public static object Team(JsonElement args)
    {
        RequireServer("stress_team");
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("seed", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt32(out int seed)
            || seed < 0 || seed > StressArgs.MaxSeed)
            throw new ArgumentException($"args.seed is required (0..{StressArgs.MaxSeed})");
        Entity heart = Entity.Null;
        if (args.TryGetProperty("heart", out var h) && h.ValueKind == JsonValueKind.String)
        {
            var parts = (h.GetString() ?? "").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hv))
                throw new ArgumentException("args.heart must be \"index:version\"");
            heart = new Entity { Index = hi, Version = hv };
        }
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("no server world; wait until ping reports a Server world");
        var em = world.EntityManager;

        GhostOps.TryPlayerTeamPrefab(world, out var prefab, out var prefabGuid, out var prefabWhy);
        var reply = new Dictionary<string, object?>
        {
            ["seed"] = seed,
            ["playerTeamPrefab"] = prefab == Entity.Null ? null : new Dictionary<string, object?> { ["guid"] = prefabGuid, ["entity"] = Fmt(prefab) },
            ["playerTeamPrefabError"] = prefab == Entity.Null ? prefabWhy : null,
        };
        Entity castleTeam = Entity.Null;
        List<Entity> castleAllies = new();
        if (heart != Entity.Null)
        {
            GhostOps.ObserveClaim(em, Entity.Null, heart, out castleTeam, out castleAllies);
            reply["heart"] = new Dictionary<string, object?>
            {
                ["entity"] = Fmt(heart),
                ["exists"] = em.Exists(heart),
                ["castleTeam"] = castleTeam == Entity.Null ? null : Fmt(castleTeam),
                ["castleTeamAllies"] = castleAllies.ConvertAll(Fmt),
            };
        }

        var owners = new List<object>();
        foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
        {
            User u;
            try { u = em.GetComponentData<User>(e); } catch { continue; }
            if (!StressIdentity.TrySeedOf(u.PlatformId, out int userSeed, out _) || userSeed != seed) continue;
            var seen = GhostOps.ObserveOfflineUserTeam(em, e, out var team, out var allies);
            var decision = OfflineUserTeamRules.Decide(seen);
            string name;
            try { name = u.CharacterName.ToString(); } catch { name = "?"; }
            var row = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["entity"] = Fmt(e),
                ["userIndex"] = u.Index,
                ["team"] = seen.HasTeamReference ? Fmt(team) : null,
                ["teamState"] = decision.Action == OfflineUserTeamAction.Ready ? "ready" : decision.Action == OfflineUserTeamAction.Create ? "missing" : decision.Code,
                ["missing"] = OfflineUserTeamRules.Missing(seen),
                ["allies"] = allies.ConvertAll(Fmt),
            };
            if (heart != Entity.Null)
                row["claimRefusal"] = OfflineUserTeamRules.ClaimCastleRefusal(GhostOps.ObserveClaim(em, e, heart, out _, out _));
            owners.Add(row);
        }
        reply["owners"] = owners;
        reply["count"] = owners.Count;
        return reply;
    }

    /// <summary>Dev unload: stop the job between steps. What it already made stays (stress_clear removes it).</summary>
    internal static void Shutdown()
    {
        try { s_job?.Cancel("Dev unloaded"); } catch { /* best-effort */ }
        try { s_pending?.SetException(new InvalidOperationException("Dev unloaded while the stress job ran; read stress_status after reload")); } catch { }
        s_pending = null;
        s_world = null;
    }

    private static object Start(World world, StressJob job, int waitMs, bool needsFinish)
    {
        s_job = job;
        s_world = world;
        var deferred = new DeferredResult(waitMs);
        s_pending = deferred;
        s_pendingNeedsFinish = needsFinish;
        try
        {
            MainThread.PostDelayed(0, Tick, TickOwner);
        }
        catch (Exception e)
        {
            job.Cancel("could not schedule: " + e.Message);
            deferred.SetException(e);
            s_pending = null;
            throw;
        }
        return deferred;
    }

    private static void Tick()
    {
        var job = s_job;
        if (job == null) return;
        try
        {
            var world = s_world;
            if (world == null || !world.IsCreated || !ReferenceEquals(world, Progenitor.Worlds.Server))
                job.Cancel("the server world changed");
            else
                job.Tick(() => Clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("stress job tick: " + e);
            job.Cancel("tick threw: " + e.Message);
        }

        var pending = s_pending;
        if (pending != null && (s_pendingNeedsFinish ? job.Finished : job.Planned))
        {
            s_pending = null;
            try { pending.SetResult(job.Describe()); }
            catch (Exception e) { pending.SetException(e); }
        }

        if (job.Finished)
        {
            Plugin.Logger?.LogInfo($"stress {job.Kind} seed {job.Args.Seed}: {job.Phase} {job.Reason} " +
                $"(steps {job.StepsDone}/{job.StepsTotal}, ticks {job.Ticks}, max tick {job.MaxTickMs:0.0} ms)");
            s_world = null;
            return;
        }
        MainThread.PostDelayed(0, Tick, TickOwner);
    }

    internal static void RequireServer(string op)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException(op + " runs on the server only");
    }

    internal static World RequireIdle()
    {
        var running = s_job;
        if (running != null && !running.Finished)
            throw new InvalidOperationException($"a stress {running.Kind} job (seed {running.Args.Seed}) is still running ({running.Phase}); read stress_status");
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("no server world; wait until ping reports a Server world");
        return world;
    }

    /// <summary>The test-server and save gate; a dry run skips only the confirm word.</summary>
    internal static void RequireGate(string? confirm, bool dryRun)
    {
        var inputs = new StressGateInputs
        {
            ProcessPath = SafePath(() => Environment.ProcessPath),
            GameRoot = SafePath(() => Paths.GameRootPath),
            ConfigPath = SafePath(() => Paths.ConfigPath),
            PersistentDataPath = SafePath(() => StressGate.PersistentDataPathFrom(Environment.GetCommandLineArgs())),
            Confirm = dryRun ? StressArgs.ConfirmWord : confirm,
        };
        if (!StressGate.Check(inputs, out var code, out var reason))
        {
            Plugin.Logger?.LogWarning("stress gate " + code + ": " + reason);
            throw new InvalidOperationException(code + ": " + reason);
        }
    }

    private static string? SafePath(Func<string?> get)
    {
        try { return get(); } catch { return null; }
    }

    private static string Fmt(Entity e) => $"{e.Index}:{e.Version}";

    /// <summary>The live world side of a job. Main thread only.</summary>
    private sealed class LiveBackend : IStressBackend
    {
        private const string PlacementMissing =
            "no proven path places a building for an offline owner: placement is live-verified only as a BuildTileModelEvent " +
            "sent by a connected player's own client (bot_event build), the owner has no character, and a server-side " +
            "BuildTileModelEvent with FromCharacter is not live-verified; spawning the prefab directly gives no castle hookup";

        private readonly World _world;
        private readonly EntityManager _em;
        private List<Entity> _territories = new();
        private Dictionary<(int, int), CastlePlot> _plots = new();
        private int _at;
        private HashSet<int>? _usedIndexes;
        private HashSet<string>? _usedNames;
        private readonly Dictionary<string, Entity> _clearTargets = new(StringComparer.Ordinal);

        public LiveBackend(World world)
        {
            _world = world;
            _em = world.EntityManager;
        }

        public bool Proven(StressStepKind kind, out string missing)
        {
            switch (kind)
            {
                case StressStepKind.Owner:
                    missing = "";
                    return true;
                case StressStepKind.Clan:
                    missing = "no proven server path creates a clan (ClanTeam entity) or adds members; ctx.Clans only reads";
                    return false;
                case StressStepKind.Heart:
                    missing = "castle heart: " + PlacementMissing;
                    return false;
                case StressStepKind.Fuel:
                    missing = "needs a placed heart; putting blood essence into a heart's fuel slots is not live-verified";
                    return false;
                case StressStepKind.Floor:
                case StressStepKind.Station:
                case StressStepKind.Chest:
                    missing = PlacementMissing;
                    return false;
                case StressStepKind.Fill:
                    missing = "needs placed chests (the item add itself, ServerGameManager.TryAddInventoryItem, is the give op's path)";
                    return false;
                default:
                    missing = "";
                    return true;
            }
        }

        public int BeginDiscovery()
        {
            _territories = BotTerritories.TerritoryEntities(_em);
            _plots = BotTerritories.PlotsByTerritory(_world);
            _at = 0;
            return _territories.Count;
        }

        public bool DiscoverNext(out StressTerritory? territory)
        {
            territory = null;
            if (_at >= _territories.Count) return false;
            var e = _territories[_at++];
            try { territory = BotTerritories.ReadForStress(_em, e, _plots); }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"stress discovery: territory {Fmt(e)}: {ex.Message}");
                territory = null;
            }
            return true;
        }

        public void CountExisting(int seed, out int sameSeed, out int otherSeeds)
        {
            sameSeed = otherSeeds = 0;
            foreach (var e in Users())
            {
                if (!TryUser(e, out var u)) continue;
                if (!StressIdentity.TrySeedOf(u.PlatformId, out int s, out _)) continue;
                if (s == seed) sameSeed++;
                else otherSeeds++;
            }
        }

        /// <summary>Every User's index and name, online or offline (disabled included). False when a User could not be read.</summary>
        private bool ReadUsedIdentity(out HashSet<int> indexes, out HashSet<string> names, out int unreadable)
        {
            indexes = new HashSet<int>();
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            unreadable = 0;
            foreach (var e in Users())
            {
                if (!TryUser(e, out var u)) { unreadable++; continue; }
                indexes.Add(u.Index);
                try { names.Add(u.CharacterName.ToString()); } catch { }
            }
            return unreadable == 0;
        }

        public StressOutcome ReserveOwnerIndexes(IReadOnlyList<StressOwnerPlan> owners)
        {
            if (!ReadUsedIdentity(out var used, out var names, out int unreadable))
                return StressOutcome.Refuse("owner_index", $"{unreadable} User entit(ies) could not be read, so their User.Index is unknown");
            if (!StressIndexPlan.TryAllocate(used, owners.Count, out var picked, out var why))
                return StressOutcome.Refuse("owner_index", why);
            for (int i = 0; i < owners.Count; i++) owners[i].UserIndex = picked[i];
            _usedIndexes = used;
            _usedNames = names;
            Plugin.Logger?.LogInfo($"stress owner indexes reserved: {string.Join(",", picked)} (existing users {used.Count})");
            return StressOutcome.Done(string.Join(",", picked));
        }

        public StressOutcome CreateOwner(StressOwnerPlan owner)
        {
            if (_usedIndexes == null || _usedNames == null || owner.UserIndex < 0)
                return StressOutcome.Refuse("owner_index", "no User.Index was reserved for " + owner.Name);
            if (_usedNames.Contains(owner.Name))
                return StressOutcome.Refuse("name_in_use", "a user named " + owner.Name + " already exists");
            // Re-read right before the write: a player who joined since the reservation got max + 1.
            if (!ReadUsedIdentity(out var nowUsed, out _, out _) || nowUsed.Contains(owner.UserIndex)
                || MaxOf(nowUsed) > StressIndexPlan.MaxOwnerIndex)
                return StressOutcome.Refuse("owner_index", $"User.Index {owner.UserIndex} reserved for {owner.Name} is no longer safe (taken, a user is unreadable, or a user now holds an index above {StressIndexPlan.MaxOwnerIndex})");
            int index = owner.UserIndex;

            var entity = GhostOps.CreateOfflineUser(_em, index, owner.PlatformId, owner.Name, out var readback);
            // The owner's own team, the way the server gives one to a new user; ClaimCastle needs it.
            var team = GhostOps.EnsureOfflineUserTeam(_world, entity);
            if (!team.Ok)
            {
                GhostOps.TryDestroyGhostEntity(_em, entity);
                return StressOutcome.Refuse(team.Code, owner.Name + ": " + team.Detail);
            }
            _usedIndexes.Add(readback.Index);
            _usedNames.Add(owner.Name);
            Plugin.Logger?.LogInfo($"stress owner created {owner.Name} entity {Fmt(entity)} index {readback.Index} platformId {readback.PlatformId} team {Fmt(team.Team)}");
            return StressOutcome.Done($"entity {Fmt(entity)} index {readback.Index} team {Fmt(team.Team)}");
        }

        public StressOutcome CreateClan(StressClanPlan clan, StressPlan plan) => Unproven(StressStepKind.Clan);
        public StressOutcome PlaceHeart(StressPlotPlan plot, StressOwnerPlan owner) => Unproven(StressStepKind.Heart);
        public StressOutcome FuelHeart(StressPlotPlan plot) => Unproven(StressStepKind.Fuel);
        public StressOutcome PlaceFloor(StressPlotPlan plot, StressCell cell) => Unproven(StressStepKind.Floor);
        public StressOutcome PlaceStation(StressPlotPlan plot, StressStationPlan station) => Unproven(StressStepKind.Station);
        public StressOutcome PlaceChest(StressPlotPlan plot, StressChestPlan chest) => Unproven(StressStepKind.Chest);
        public StressOutcome FillChest(StressPlotPlan plot, StressChestPlan chest) => Unproven(StressStepKind.Fill);

        private StressOutcome Unproven(StressStepKind kind)
        {
            Proven(kind, out var why);
            return StressOutcome.Refuse("unproven", why);
        }

        public List<StressClearTarget> FindSeedUsers(int seed)
        {
            _clearTargets.Clear();
            var heartsByOwner = new Dictionary<ulong, int>();
            foreach (var plot in CastleReader.Plots(_world))
            {
                if (!StressIdentity.IsStressPlatformId(plot.OwnerPlatformId)) continue;
                heartsByOwner[plot.OwnerPlatformId] = heartsByOwner.TryGetValue(plot.OwnerPlatformId, out int n) ? n + 1 : 1;
            }

            var list = new List<StressClearTarget>();
            foreach (var e in Users())
            {
                if (!TryUser(e, out var u)) continue;
                if (!StressIdentity.TrySeedOf(u.PlatformId, out int s, out int ordinal) || s != seed) continue;
                var clan = u.ClanEntity._Entity;
                var character = u.LocalCharacter._Entity;
                string name;
                try { name = u.CharacterName.ToString(); } catch { name = StressIdentity.OwnerName(ordinal); }
                var t = new StressClearTarget
                {
                    Entity = Fmt(e),
                    Name = name,
                    PlatformId = u.PlatformId,
                    Ordinal = ordinal,
                    HeartsOwned = heartsByOwner.TryGetValue(u.PlatformId, out int h) ? h : 0,
                    Clan = clan != Entity.Null && clan.Index > 0 && _em.Exists(clan) ? Fmt(clan) : "",
                    HasCharacter = character != Entity.Null && character.Index > 0 && _em.Exists(character),
                };
                _clearTargets[t.Entity] = e;
                list.Add(t);
            }
            return list;
        }

        public StressOutcome RemoveUser(StressClearTarget target)
        {
            if (!_clearTargets.TryGetValue(target.Entity, out var e) || !_em.Exists(e))
                return StressOutcome.Done("already gone");
            // Re-check identity right before the destroy: only a user still in this seed's band goes.
            if (!TryUser(e, out var u) || u.PlatformId != target.PlatformId || !StressIdentity.IsStressPlatformId(u.PlatformId))
                return StressOutcome.Refuse("identity_changed", "entity " + target.Entity + " is no longer that stress owner");
            if (!GhostOps.TryDestroyGhostEntity(_em, e))
                return StressOutcome.Refuse("destroy_failed", "see the server log");
            Plugin.Logger?.LogInfo($"stress owner removed {target.Name} entity {target.Entity} platformId {target.PlatformId}");
            return StressOutcome.Done(target.HasCharacter ? "user and character" : "user");
        }

        private static int MaxOf(HashSet<int> set)
        {
            int max = -1;
            foreach (int i in set) if (i > max) max = i;
            return max;
        }

        private List<Entity> Users() =>
            Query.Entities(_em, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));

        private bool TryUser(Entity e, out User user)
        {
            try
            {
                user = _em.GetComponentData<User>(e);
                return true;
            }
            catch
            {
                user = default;
                return false;
            }
        }
    }
}
