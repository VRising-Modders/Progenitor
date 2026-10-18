using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Progenitor.Castles;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>One occupied stress castle transfer, followed by later-frame verification.</summary>
internal static class StressTransferOps
{
    public static object Run(JsonElement args)
    {
        StressOps.RequireServer("stress_transfer");
        int plot = args.GetProperty("castle").GetInt32();
        if (plot < 0) throw new ArgumentException("castle must be a territory index");
        string? raw = args.GetProperty("toPlatformId").GetString();
        if (!ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
            throw new ArgumentException("toPlatformId must be a decimal string");
        bool dry = args.TryGetProperty("dryRun", out var d) && d.GetBoolean();
        bool experiment = args.TryGetProperty("experiment", out var x) && x.GetBoolean();
        string? confirm = args.TryGetProperty("confirm", out var c) ? c.GetString() : null;
        StressOps.RequireGate(confirm, dry);
        if (!dry && !experiment) throw new InvalidOperationException("experiment_required");
        var world = StressStageOps.RequireIdle();
        var em = world.EntityManager;
        var plots = CastleReader.Plots(world).ToArray();
        var matches = plots.Where(p => p.TerritoryIndex == plot).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("castle_not_found");
        var castle = matches[0];
        Entity heart = castle.HeartEntity, old = castle.OwnerUserEntity;
        var users = Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly))
            .Where(e => em.GetComponentData<User>(e).PlatformId == id).ToArray();
        if (users.Length != 1) throw new InvalidOperationException("target_not_unique");
        Entity target = users[0];
        if (old == Entity.Null || !em.Exists(old) || !old.Has<User>(em)) throw new InvalidOperationException("old_owner_missing");
        if (old == target) throw new InvalidOperationException("already_owner");
        var before = Observe(world, plot, old, target, heart);
        var oldData = em.GetComponentData<User>(old);
        var newData = em.GetComponentData<User>(target);
        var settings = SettingsManager.ServerGameSettings ?? throw new InvalidOperationException("settings_unavailable");
        var limits = settings.CastleStatModifiers_Global;
        string? refusal = StressTransferRules.Refusal(plot, StressIdentity.IsStressPlatformId(oldData.PlatformId),
            StressTransferRules.AllowedIdentity(id), before.TargetComplete,
            !oldData.IsConnected && !newData.IsConnected && !old.Has<IsConnected>(em) && !target.Has<IsConnected>(em)
                && !old.Has<ConnectedUser>(em) && !target.Has<ConnectedUser>(em),
            oldData.ClanEntity._Entity == Entity.Null && newData.ClanEntity._Entity == Entity.Null,
            before.LinksValid, before.NewPlots.Length, before.NewCount, limits.CastleLimit);
        var claim = GhostOps.ObserveClaim(em, target, heart, out _, out _);
        refusal ??= OfflineUserTeamRules.ClaimCastleRefusal(claim);
        if (dry || refusal != null) return new { dryRun = dry, applied = false, refusal, before, castleLimit = limits.CastleLimit };

        // The generated wrapper is the same one used by the qualified castle harness.
        try { TeamUtility.ClaimCastle(em, target, heart, limits.CastleHeartLimitType); }
        catch (Exception e)
        {
            return new { applied = (bool?)null, verified = false, error = "Claim attempted; inspect before retrying: " + e.Message, before };
        }
        var deferred = new DeferredResult(10_000);
        MainThread.PostDelayed(2, () =>
        {
            try
            {
                if (!world.IsCreated || !ReferenceEquals(world, Worlds.Server)) throw new InvalidOperationException("world_changed");
                var after = Observe(world, plot, old, target, heart);
                bool verified = after.Owner == Fmt(target) && after.LastOwner == Fmt(target)
                    && after.CastleAllies.SequenceEqual(new[] { after.NewTeam })
                    && !after.OldAllies.Contains(after.CastleTeam) && after.NewAllies.Count(a => a == after.CastleTeam) == 1
                    && after.OldPlots.SequenceEqual(before.OldPlots.Where(p => p != plot))
                    && after.NewPlots.SequenceEqual(before.NewPlots.Append(plot).OrderBy(p => p))
                    && after.NewCount == after.NewPlots.Length
                    && (before.OldCount == null ? after.OldCount == null : after.OldCount == after.OldPlots.Length)
                    && after.TargetComplete;
                deferred.SetResult(new { applied = true, verified, before, after });
            }
            catch (Exception e) { deferred.SetResult(new { applied = true, verified = false, error = e.Message, before }); }
        }, deferred);
        return deferred;
    }

    private sealed class Snapshot
    {
        public string Owner { get; set; } = "";
        public string LastOwner { get; set; } = "";
        public string OldTeam { get; set; } = "";
        public string NewTeam { get; set; } = "";
        public string CastleTeam { get; set; } = "";
        public string[] OldAllies { get; set; } = Array.Empty<string>();
        public string[] NewAllies { get; set; } = Array.Empty<string>();
        public string[] CastleAllies { get; set; } = Array.Empty<string>();
        public int[] OldPlots { get; set; } = Array.Empty<int>();
        public int[] NewPlots { get; set; } = Array.Empty<int>();
        public int? OldCount { get; set; }
        public int? NewCount { get; set; }
        public bool TargetComplete { get; set; }
        public bool LinksValid { get; set; }
        public string Controlled { get; set; } = "";
        public string LocalCharacter { get; set; } = "";
        public string Progression { get; set; } = "";
        public bool IsConnectedComponent { get; set; }
    }

    private static Snapshot Observe(World world, int plot, Entity old, Entity target, Entity heart)
    {
        var em = world.EntityManager;
        var oldShape = GhostOps.ObserveOfflineUserTeam(em, old, out var oldTeam, out var oldAllies);
        var newShape = GhostOps.ObserveOfflineUserTeam(em, target, out var newTeam, out var newAllies);
        GhostOps.ObserveClaim(em, target, heart, out var castleTeam, out var castleAllies);
        GhostOps.TryReadEntityField<UserOwner>(em, heart, Layout.UserOwnerOwnerOffset, out var owner);
        var last = em.GetComponentData<CastleHeart>(heart).LastUserOwner._Entity;
        bool controller = GhostOps.TryReadEntityField<Controller>(em, target, Layout.ControllerControlledOffset, out var controlled);
        bool progression = GhostOps.TryReadEntityField<ProgressionMapper>(em, target, Layout.ProgressionMapperEntityOffset, out var prog)
            && prog != Entity.Null && em.Exists(prog);
        var local = em.GetComponentData<User>(target).LocalCharacter._Entity;
        bool ownCharacter = GhostOps.TryReadEntityField<PlayerCharacter>(em, controlled, Layout.StressPlayerCharacterUserOffset, out var characterUser)
            && characterUser == target
            && GhostOps.TryReadEntityField<ControlledBy>(em, controlled, Layout.StressControlledByControllerOffset, out var controllerUser)
            && controllerUser == target;
        var plots = CastleReader.Plots(world).ToArray();
        return new Snapshot
        {
            Owner = Fmt(owner), LastOwner = Fmt(last), OldTeam = Fmt(oldTeam), NewTeam = Fmt(newTeam), CastleTeam = Fmt(castleTeam),
            OldAllies = oldAllies.Select(Fmt).ToArray(), NewAllies = newAllies.Select(Fmt).ToArray(), CastleAllies = castleAllies.Select(Fmt).ToArray(),
            OldPlots = plots.Where(p => p.OwnerUserEntity == old).Select(p => p.TerritoryIndex).OrderBy(p => p).ToArray(),
            NewPlots = plots.Where(p => p.OwnerUserEntity == target).Select(p => p.TerritoryIndex).OrderBy(p => p).ToArray(),
            OldCount = Count(em, old), NewCount = Count(em, target),
            Controlled = Fmt(controlled), LocalCharacter = Fmt(local), Progression = Fmt(prog), IsConnectedComponent = target.Has<IsConnected>(em),
            TargetComplete = controller && controlled != Entity.Null && controlled == local && em.Exists(controlled)
                && controlled.Has<Health>(em) && ownCharacter && progression && target.Has<UserHeartCount>(em),
            LinksValid = oldShape.TeamPointsAtUser && newShape.TeamPointsAtUser && oldShape.TeamHasAllies && newShape.TeamHasAllies
                && oldTeam != newTeam && oldTeam != castleTeam && newTeam != castleTeam
                && castleAllies.Count == 1 && castleAllies[0] == oldTeam
                && oldAllies.Count(a => a == castleTeam) == 1 && !newAllies.Contains(castleTeam)
                && owner == old && last == old,
        };
    }

    private static unsafe int? Count(EntityManager em, Entity user)
    {
        if (!user.Has<UserHeartCount>(em)) return null;
        var type = ComponentIndex.Of<UserHeartCount>(ComponentType.AccessMode.ReadOnly);
        var size = TypeManager.GetTypeInfo(type.TypeIndex).TypeSize;
        if (size < Layout.StressUserHeartCountOffset + sizeof(int)) return null;
        var p = (byte*)em.GetComponentDataRawRO(user, type.TypeIndex);
        return p == null ? null : *(int*)(p + Layout.StressUserHeartCountOffset);
    }
    private static string Fmt(Entity e) => e.Index + ":" + e.Version;
}
