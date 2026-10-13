using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Abilities;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Persistence;
using Progenitor.Plugins;
using Progenitor.Systems;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Reuse the Wendigo nova's ring payload at both edges of the player's travel cast.
/// The nova is not another cast: no replacement slot, shared cast-time edit, recovery retry,
/// or proxy character is needed. Native initialization, live evidence and the limits of this
/// specific recipe are in the internal research notes.
/// </summary>
internal static class SampleFrostDash
{
    internal const int FrostNovaGroup = 1238313774;
    internal const int FrostNovaRing = -1227952025;
    internal const int TravelSlot = 2;
    internal const string SystemName = "sample.frostdash";
    /// <summary>
    /// The <c>.frostdash on|off</c> switch, kept in the world store (<c>ctx.Save</c>) on purpose: a
    /// demo toggle that a new world after a wipe starts without, and that a restore takes back to
    /// what that world had. A switch an operator sets for the whole server belongs in config as a
    /// Server-wide setting instead (docs/modules.md "Where should my data go?").
    /// </summary>
    internal const string EnabledKey = "frostdash.enabled";
    internal const string UsageReply = "Usage: .frostdash on | off | status | dash. Spawn a frost nova at dash departure and landing. 'dash' requests the equipped travel ability.";
    internal static bool Enabled { get; private set; }
    internal static int Fired { get; private set; }
    internal enum Kind { Bad, On, Off, Status, Dash }
    internal static bool TryParse(string what, out Kind kind)
    {
        kind = Kind.Bad;
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "": case "status": kind = Kind.Status; return true;
            case "on": kind = Kind.On; return true;
            case "off": kind = Kind.Off; return true;
            case "dash": kind = Kind.Dash; return true;
            default: return false;
        }
    }
    internal static bool IsDashGroup(string? name) => name != null
        && name.StartsWith("AB_Vampire_Veil", StringComparison.OrdinalIgnoreCase)
        && (name.EndsWith("_Group", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_AbilityGroup", StringComparison.OrdinalIgnoreCase));
    internal static long KeyOf(int index, int version) => ((long)index << 32) | (uint)version;
    internal static string StatusReply(bool enabled, int fired) => "Frost dash is " + (enabled ? "ON" : "off")
        + ". Nova payloads spawned this session: " + fired.ToString(CultureInfo.InvariantCulture) + ".";
    internal static string OnReply() => "Frost dash ON. Native frost rings at departure and landing, with no additional cast animation.";
    internal static string OffReply(int fired) => "Frost dash off after " + fired.ToString(CultureInfo.InvariantCulture) + " payload(s); active owned rings stopped.";
    internal static string DashReply(bool ok, string detail) => (ok ? "Dash requested: " : "Could not request dash: ") + detail;

#if !PROGENITOR_TESTS
    private sealed class PlayerState
    {
        public Entity Character;
        public float3 Before;
        public int TravelGroup;
        public FrostDashEdgeTracker Edges = null!;
        public Entity StartRing;
        public bool EmittedStart;
    }
    private static readonly Dictionary<long, PlayerState> Players = new();
    private static readonly List<Entity> Rings = new();
    private static IDisposable? _before, _after;
    private static IModStore? _store;
    private static World? _world;

    internal static void Attach(PluginContext ctx)
    {
        ctx.Commands.Register(typeof(SampleFrostDashCommands));
        _store = ctx.Save;
        _before = ctx.Systems.Add(SystemName + ".before", VirtualSystemAnchor.BeforeSimulation, BeforeTick);
        _after = ctx.Systems.Add(SystemName, OnTick);
        Enabled = _store.Get<bool>(EnabledKey, false);
        Plugin.Logger?.LogInfo("Sample frost dash: native ring recipe loaded; enabled=" + Enabled);
    }
    internal static void Detach()
    {
        _before?.Dispose(); _after?.Dispose(); _before = _after = null;
        ClearRings(); Players.Clear(); _world = null; _store = null; Enabled = false;
    }
    internal static void SetEnabled(bool on)
    {
        if (Enabled != on) { ClearRings(); Players.Clear(); }
        Enabled = on;
        _store?.Set(EnabledKey, on);
    }
    private static void ClearRings()
    {
        if (_world != null && _world.IsCreated)
        {
            var em = _world.EntityManager;
            var destroyed = new List<string>();
            var failed = new List<string>();
            int alive = 0;
            foreach (var ring in Rings)
                if (em.Exists(ring))
                {
                    alive++;
                    var id = ring.Index.ToString(CultureInfo.InvariantCulture) + ":" + ring.Version.ToString(CultureInfo.InvariantCulture);
                    try { DestroyUtility.Destroy(em, ring, DestroyDebugReason.TryRemoveBuff); destroyed.Add(id); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Frost ring cleanup: " + e.Message); failed.Add(id); }
                }
            var line = "FROSTDASH cleanup alive=" + alive.ToString(CultureInfo.InvariantCulture)
                + " destroyed=[" + string.Join(",", destroyed) + "]";
            if (failed.Count > 0) line += " failed=[" + string.Join(",", failed) + "]";
            Plugin.Logger?.LogInfo(line);
        }
        Rings.Clear();
    }
    private static void BeforeTick(VirtualSystemContext ctx)
    {
        if (!Enabled) return;
        try
        {
            if (_world != ctx.World) { ClearRings(); Players.Clear(); _world = ctx.World; }
            var em = ctx.EntityManager;
            var seen = new HashSet<long>();
            foreach (var entity in Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
            {
                if (!entity.TryRead<User>(em, out var user) || !user.IsConnected || user.IsBot) continue;
                var character = user.LocalCharacter._Entity;
                if (character == Entity.Null || !em.Exists(character)
                    || !character.TryRead<Translation>(em, out var position)
                    || !AbilityCast.TryReadCastState(ctx.World, character, out _, out var current)) continue;
                int travel = 0;
                if (AbilityCast.TryGetSlotGroup(ctx.World, character, TravelSlot, out var equipped)
                    && PrefabLookup.TryGetName(ctx.World, equipped, out var name) && IsDashGroup(name)) travel = equipped.GuidHash;
                long key = KeyOf(character.Index, character.Version);
                seen.Add(key);
                if (!Players.TryGetValue(key, out var state))
                    Players[key] = state = new PlayerState { Character = character,
                        Edges = new FrostDashEdgeTracker(travel != 0 && current.GuidHash == travel) };
                state.Before = position.Value;
                state.TravelGroup = travel;
            }
            foreach (var key in new List<long>(Players.Keys)) if (!seen.Contains(key)) Players.Remove(key);
            for (int i = Rings.Count - 1; i >= 0; i--) if (!em.Exists(Rings[i])) Rings.RemoveAt(i);
        }
        catch (Exception e) { Plugin.Logger?.LogError("Frost dash before tick: " + e); }
    }
    private static void OnTick(VirtualSystemContext ctx)
    {
        if (!Enabled || _world != ctx.World) return;
        try
        {
            var em = ctx.EntityManager;
            foreach (var state in Players.Values)
            {
                if (!AbilityCast.TryReadCastState(ctx.World, state.Character, out var counter, out var group)
                    || !state.Character.TryRead<Translation>(em, out var now)) continue;
                var edge = state.Edges.Observe(state.TravelGroup != 0 && group.GuidHash == state.TravelGroup);
                if (edge == FrostDashEdge.None) continue;
                if (edge == FrostDashEdge.Start) { state.StartRing = Entity.Null; state.EmittedStart = false; }
                else if (!state.EmittedStart) continue;
                var point = edge == FrostDashEdge.Start ? state.Before : now.Value;
                if (edge == FrostDashEdge.Start && Rings.Count + Players.Count >= 128) { Plugin.Logger?.LogWarning("Frost dash active-ring limit reached"); continue; }
                var ring = SpawnRing(ctx.World, state.Character, point);
                Fired++;
                Plugin.Logger?.LogInfo($"FROSTDASH edge={edge} frame={ctx.Frame} ring={ring} position={point} player={state.Character} castCounter={counter} group={group.GuidHash}");
                if (edge == FrostDashEdge.Start) { state.StartRing = ring; state.EmittedStart = true; }
                else if (em.Exists(state.StartRing) && state.StartRing.TryRead<Translation>(em, out var start))
                    Plugin.Logger?.LogInfo($"FROSTDASH departure ring remains at={start.Value}; landing={point}; distance={math.distance(start.Value, point):0.000}");
            }
        }
        catch (Exception e) { Plugin.Logger?.LogError("Frost dash after tick: " + e); }
    }
    private static Entity SpawnRing(World world, Entity player, float3 position)
    {
        var em = world.EntityManager;
        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(FrostNovaRing), out var prefab))
            throw new InvalidOperationException("Wendigo frost nova ring prefab is unavailable");
        // Native helper copies owner, networked creator and team reference. SpawnTag + Disabled
        // are intentional: the normal spawn systems initialize and enable the ring next tick.
        // Live-tested for this payload; do not generalize this recipe to arbitrary prefabs.
        var ring = ProjectM.Scripting.GameManager_Shared.InstantiateEntityImmediate(em, player, prefab);
        Rings.Add(ring); // Retain ownership even if an instance customization throws.
        // The prefab otherwise takes its owner's position on spawn, after the dash moved.
        // Change only the new instance; authored damage, hit filters and lifetime remain.
        em.RemoveComponent(ring, ComponentIndex.Of<GetTranslationOnSpawn>(ComponentType.AccessMode.ReadWrite));
        em.SetComponentData(ring, new Translation { Value = position });
        SampleFrostNovaAppearance.Track(world, ring);
        return ring;
    }
    internal static bool TryForceDash(out string detail)
    {
        detail = "no connected player with a living character and equipped Veil";
        var world = Progenitor.Worlds.Server;
        if (world == null) return false;
        var em = world.EntityManager;
        foreach (var entity in Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
        {
            if (!entity.TryRead<User>(em, out var user) || !user.IsConnected || user.IsBot) continue;
            var character = user.LocalCharacter._Entity;
            if (!em.Exists(character) || !character.TryRead<Health>(em, out var health) || health.Value <= 0) continue;
            if (!AbilityCast.TryGetSlotGroup(world, character, TravelSlot, out var travel)
                || !PrefabLookup.TryGetName(world, travel, out var name) || !IsDashGroup(name)) continue;
            if (!AbilityCast.Cast(world, character, travel, out detail)) return false;
            detail = name + " on " + character + " (request only; inspect dash edge logs for execution)";
            return true;
        }
        return false;
    }
#endif
}
#if !PROGENITOR_TESTS
internal static class SampleFrostDashCommands
{
    [Command("frostdash", adminOnly: true, usage: ".frostdash on | off | status | dash",
        description: "Admin only. Cast the mountain frost nova every time you dash.")]
    public static void FrostDash(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleFrostDash.TryParse(what, out var kind))
            {
                ctx.Reply(SampleFrostDash.UsageReply);
                return;
            }

            if (kind == SampleFrostDash.Kind.On)
            {
                SampleFrostDash.SetEnabled(true);
                ctx.Reply(SampleFrostDash.OnReply());
                return;
            }

            if (kind == SampleFrostDash.Kind.Dash)
            {
                bool ok = SampleFrostDash.TryForceDash(out string detail);
                ctx.Reply(SampleFrostDash.DashReply(ok, detail));
                return;
            }

            if (kind == SampleFrostDash.Kind.Off)
            {
                SampleFrostDash.SetEnabled(false);
                ctx.Reply(SampleFrostDash.OffReply(SampleFrostDash.Fired));
                return;
            }

            ctx.Reply(SampleFrostDash.StatusReply(SampleFrostDash.Enabled, SampleFrostDash.Fired));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .frostdash threw: " + e);
            ctx.Reply("Sample frostdash failed. See the server log.");
        }
    }
}
#endif

