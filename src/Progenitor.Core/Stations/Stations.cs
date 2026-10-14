using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Content;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Stations;

/// <summary>A recipe station found by <see cref="Stations.Near"/>.</summary>
public sealed class StationInfo
{
    /// <summary>The station entity. Valid for this world load only; do not persist it.</summary>
    public Entity Station { get; init; }

    /// <summary>The station's prefab GUID hash.</summary>
    public int PrefabGuid { get; init; }

    /// <summary>The station's prefab name; empty when unknown.</summary>
    public string Name { get; init; } = "";

    /// <summary>Refinement station or crafting workstation.</summary>
    public StationKind Kind { get; init; }

    /// <summary>World position.</summary>
    public float3 Position { get; init; }

    /// <summary>Horizontal distance from the point the search was made at.</summary>
    public float Distance { get; init; }
}

/// <summary>
/// Server reads and the per-recipe switch for castle recipe stations. <c>ctx.Stations</c> is the
/// plugin-bound surface. Every member here throws on the client; the pure craft count (<see cref="StationLogic.CraftsPossible(StationRecipe, IReadOnlyDictionary{int, long})"/>, also on <c>ctx.Stations</c>) works anywhere.
///
/// <para>Refinement stations keep their recipe list on the instance in
/// <c>RefinementstationRecipesBuffer</c> (recipe, <c>Unlocked</c>, <c>Disabled</c>; networked, so a
/// change reaches every client's station window). Their work state is <c>Refinementstation</c>
/// (<c>Status</c>, <c>CurrentRecipeGuid</c>, <c>RefiningStartTime</c> in server time). Crafting
/// workstations list recipes in <c>WorkstationRecipesBuffer</c> and work through
/// <c>QueuedWorkstationCraftAction</c>, head row first. Recipe details come from the recipe prefab:
/// <c>RecipeData</c>, <c>RecipeRequirementBuffer</c>, <c>RecipeOutputBuffer</c>.</para>
///
/// <para><see cref="SetRecipeEnabled"/> makes the same writes as the game's
/// <c>ToggleRefiningRecipeSystem</c> (the station window's checkbox): the row's <c>Disabled</c>,
/// the rows of recipes the recipe prefab links in <c>RecipeLinkBuffer</c>, and
/// <c>CastleWorkstation.ForceUpdateInStation</c> so the refining system re-picks a recipe on its
/// next update. It writes directly on the server, so no player needs the station open.</para>
///
/// <para>All reads and writes are raw at the declared <see cref="Layout"/> offsets and strides
/// through the non-generic buffer and component accessors.</para>
/// </summary>
public static unsafe class Stations
{
    // Most rows read from one recipe prefab's input, output or link buffer (a sanity bound on prefab
    // data). A station's own recipe list is read in full, so the toggle's length check never trips on it.
    private const int MaxPrefabRows = 256;

    /// <summary>The recipe machinery <paramref name="station"/> runs.</summary>
    public static StationKind KindOf(World world, Entity station)
    {
        var em = RequireServer(world);
        return Kind(em, station);
    }

    /// <summary>
    /// The recipes <paramref name="station"/> offers, in the station's own order. Empty when it is
    /// not a recipe station or does not exist.
    /// </summary>
    public static IReadOnlyList<StationRecipe> Recipes(World world, Entity station)
    {
        var em = RequireServer(world);
        var kind = Kind(em, station);
        var list = new List<StationRecipe>();
        if (kind == StationKind.Refinement)
        {
            foreach (var row in RefinementRows(em, station))
                list.Add(Describe(world, em, row.Guid, kind, enabled: !row.Disabled, canToggle: true, unlocked: row.Unlocked));
        }
        else if (kind == StationKind.Workstation)
        {
            byte* raw = AiRuntime.Buffer<WorkstationRecipesBuffer>(em, station, Layout.WorkstationRecipesBufferStride, false, out int length);
            for (int i = 0; raw != null && i < length; i++)
            {
                int guid = *(int*)(raw + i * Layout.WorkstationRecipesBufferStride + Layout.WorkstationRecipesBufferRecipeGuidOffset);
                if (guid == 0) continue;
                list.Add(Describe(world, em, guid, kind, enabled: true, canToggle: false, unlocked: true));
            }
        }
        return list;
    }

    /// <summary>
    /// Switch one recipe on a refinement station on or off, the way the station window's checkbox
    /// does, and replicate it the same way (the networked recipe buffer). Refuses, with a reason, on a
    /// workstation (the game has no per-recipe switch there), a non-station, or a recipe the station
    /// does not list.
    /// </summary>
    public static StationToggleResult SetRecipeEnabled(World world, Entity station, int recipeGuid, bool on)
    {
        var em = RequireServer(world);
        var kind = Kind(em, station);
        if (kind != StationKind.Refinement)
            return Refuse(recipeGuid, kind == StationKind.Workstation, StationLogic.NoToggleReason(kind));

        var rows = RefinementRows(em, station);
        var shape = new List<(int RecipeGuid, bool Disabled)>(rows.Count);
        foreach (var r in rows) shape.Add((r.Guid, r.Disabled));
        var linked = LinkedRecipes(world, em, recipeGuid);
        var writes = StationLogic.PlanToggle(shape, recipeGuid, on, linked, out _, out int linkedChanged, out string reason);
        if (writes == null)
            return Refuse(recipeGuid, false, reason);
        if (writes.Count == 0)
            return new StationToggleResult { Ok = true, Changed = false, Enabled = on, RecipeGuid = recipeGuid, Reason = reason };

        bool enabledNow = StationLogic.IsEnabled(shape, recipeGuid);

        // Every guard runs before the first byte is written, so a refusal never leaves a partial
        // toggle: the CastleWorkstation overlay first, then the recipe buffer and its rows.
        byte* cw = null;
        if (em.HasComponent(station, ComponentIndex.Of<CastleWorkstation>(ComponentType.AccessMode.ReadOnly)))
        {
            try
            {
                cw = AiRuntime.RW<CastleWorkstation>(em, station, Layout.CastleWorkstationForceUpdateInStationOffset + 1);
            }
            catch (InvalidOperationException e)
            {
                return Refuse(recipeGuid, enabledNow, "the station's CastleWorkstation cannot be written (" + e.Message + "); nothing was written");
            }
        }

        int stride = Layout.RefinementstationRecipesBufferStride;
        byte* rw;
        int length;
        try
        {
            rw = AiRuntime.Buffer<RefinementstationRecipesBuffer>(em, station, stride, true, out length);
        }
        catch (InvalidOperationException e)
        {
            return Refuse(recipeGuid, enabledNow, "the station's recipe list cannot be written (" + e.Message + "); nothing was written");
        }
        if (rw == null || length != rows.Count)
            return Refuse(recipeGuid, enabledNow, "the station's recipe list changed while it was being written; nothing was written");
        foreach (int i in writes)
        {
            if (*(int*)(rw + i * stride + Layout.RefinementstationRecipesBufferRecipeGuidOffset) != shape[i].RecipeGuid)
                return Refuse(recipeGuid, enabledNow, "the station's recipe list changed while it was being written; nothing was written");
        }
        foreach (int i in writes)
            rw[i * stride + Layout.RefinementstationRecipesBufferDisabledOffset] = on ? (byte)0 : (byte)1;
        if (cw != null)
            cw[Layout.CastleWorkstationForceUpdateInStationOffset] = 1;
        Plugin.Logger?.LogInfo("[stations] recipe " + recipeGuid + " " + (on ? "enabled" : "disabled") + " on station "
            + station.Index + ":" + station.Version + " (" + writes.Count + " row(s), " + linkedChanged + " linked)");
        return new StationToggleResult
        {
            Ok = true,
            Changed = true,
            Enabled = on,
            RecipeGuid = recipeGuid,
            LinkedRowsChanged = linkedChanged,
            Reason = reason,
        };
    }

    /// <summary>
    /// What <paramref name="station"/> is doing now. Null when it is not a recipe station.
    /// </summary>
    public static StationProgress? Progress(World world, Entity station)
    {
        var em = RequireServer(world);
        var kind = Kind(em, station);
        if (kind == StationKind.Refinement)
        {
            byte* p = AiRuntime.RO<Refinementstation>(em, station, Layout.RefinementstationIsWorkingOffset + 1);
            if (p == null) return null;
            int status = *(int*)(p + Layout.RefinementstationStatusOffset);
            int recipe = *(int*)(p + Layout.RefinementstationCurrentRecipeGuidOffset);
            double start = *(double*)(p + Layout.RefinementstationRefiningStartTimeOffset);
            double now = ServerNow(em);
            return StationLogic.RefinementProgress(status, recipe, now, start, Duration(world, em, recipe));
        }
        if (kind == StationKind.Workstation)
        {
            int stride = Layout.QueuedWorkstationCraftActionStride;
            byte* q = AiRuntime.Buffer<QueuedWorkstationCraftAction>(em, station, stride, false, out int length);
            if (q == null || length <= 0)
                return StationLogic.WorkstationProgress(0, 0, 0, 0, 0);
            int recipe = *(int*)(q + Layout.QueuedWorkstationCraftActionRecipeGuidOffset);
            float progress = *(float*)(q + Layout.QueuedWorkstationCraftActionProgressTimeOffset);
            int status = *(int*)(q + Layout.QueuedWorkstationCraftActionStatusOffset);
            return StationLogic.WorkstationProgress(status, length, recipe, progress, Duration(world, em, recipe));
        }
        return null;
    }

    /// <summary>True when the station is converting right now (<see cref="StationState.Working"/>).</summary>
    public static bool IsWorking(World world, Entity station) => Progress(world, station)?.IsWorking ?? false;

    /// <summary>
    /// Recipe stations within <paramref name="radius"/> meters (horizontal) of
    /// <paramref name="position"/>, nearest first. Disabled entities are skipped.
    ///
    /// <para>Cost: this runs two queries over every station in the world and checks each one, so call
    /// it on demand (a command, a panel opening, a scan you schedule), not every tick. Only the stations
    /// inside the radius are allocated a result row; keep the returned entities rather than calling
    /// it again to find the same station.</para>
    /// </summary>
    public static IReadOnlyList<StationInfo> Near(World world, float3 position, float radius)
    {
        var em = RequireServer(world);
        var found = new List<StationInfo>();
        var translation = ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly);
        var refinement = ComponentIndex.Of<Refinementstation>(ComponentType.AccessMode.ReadOnly);
        // Refinement stations by their own component, then workstations by their recipe list; an
        // entity carrying both is taken in the first pass only.
        Collect(world, em, Query.Entities(em, includeDisabled: false, includeSystems: false, refinement, translation),
            position, radius, skipRefinement: false, found);
        Collect(world, em, Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<WorkstationRecipesBuffer>(ComponentType.AccessMode.ReadOnly), translation),
            position, radius, skipRefinement: true, found);
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found;
    }

    private static void Collect(World world, EntityManager em, List<Entity> entities, float3 position, float radius,
        bool skipRefinement, List<StationInfo> found)
    {
        var refinement = ComponentIndex.Of<Refinementstation>(ComponentType.AccessMode.ReadOnly);
        float2 center = position.xz;
        foreach (var e in entities)
        {
            if (skipRefinement && em.HasComponent(e, refinement)) continue;
            if (!e.TryRead<Translation>(em, out var tr)) continue;
            float d = math.distance(tr.Value.xz, center);
            if (d > radius) continue;
            var kind = Kind(em, e);
            if (kind == StationKind.None) continue;
            int guid = AiRuntime.PrefabGuidOf(em, e);
            PrefabLookup.TryGetName(world, new PrefabGUID(guid), out string name);
            found.Add(new StationInfo { Station = e, PrefabGuid = guid, Name = name, Kind = kind, Position = tr.Value, Distance = d });
        }
    }

    // ---- reads ------------------------------------------------------------------------------------

    private readonly struct RefinementRow
    {
        public readonly int Guid;
        public readonly bool Unlocked;
        public readonly bool Disabled;

        public RefinementRow(int guid, bool unlocked, bool disabled)
        {
            Guid = guid;
            Unlocked = unlocked;
            Disabled = disabled;
        }
    }

    private static StationKind Kind(EntityManager em, Entity station)
    {
        if (station == Entity.Null || !em.Exists(station)) return StationKind.None;
        if (em.HasComponent(station, ComponentIndex.Of<Refinementstation>(ComponentType.AccessMode.ReadOnly))
            && em.HasComponent(station, ComponentIndex.Of<RefinementstationRecipesBuffer>(ComponentType.AccessMode.ReadOnly)))
            return StationKind.Refinement;
        if (em.HasComponent(station, ComponentIndex.Of<WorkstationRecipesBuffer>(ComponentType.AccessMode.ReadOnly)))
            return StationKind.Workstation;
        return StationKind.None;
    }

    private static List<RefinementRow> RefinementRows(EntityManager em, Entity station)
    {
        var rows = new List<RefinementRow>();
        int stride = Layout.RefinementstationRecipesBufferStride;
        byte* raw = AiRuntime.Buffer<RefinementstationRecipesBuffer>(em, station, stride, false, out int length);
        for (int i = 0; raw != null && i < length; i++)
        {
            byte* row = raw + i * stride;
            rows.Add(new RefinementRow(
                *(int*)(row + Layout.RefinementstationRecipesBufferRecipeGuidOffset),
                row[Layout.RefinementstationRecipesBufferUnlockedOffset] != 0,
                row[Layout.RefinementstationRecipesBufferDisabledOffset] != 0));
        }
        return rows;
    }

    private static List<int> LinkedRecipes(World world, EntityManager em, int recipeGuid)
    {
        var links = new List<int>();
        if (recipeGuid == 0 || !PrefabLookup.TryGetEntity(world, new PrefabGUID(recipeGuid), out var prefab)) return links;
        byte* raw = AiRuntime.Buffer<RecipeLinkBuffer>(em, prefab, RecipeBufferLayout.LinkStride, false, out int length);
        for (int i = 0; raw != null && i < length && i < MaxPrefabRows; i++)
        {
            RecipeBufferLayout.TryReadLink(raw, i, out int guid);
            if (guid != 0) links.Add(guid);
        }
        return links;
    }

    private static StationRecipe Describe(World world, EntityManager em, int recipeGuid, StationKind kind, bool enabled, bool canToggle, bool unlocked)
    {
        PrefabLookup.TryGetName(world, new PrefabGUID(recipeGuid), out string name);
        var inputs = new List<StationItem>();
        var outputs = new List<StationItem>();
        float duration = 0;
        bool hidden = false;
        if (PrefabLookup.TryGetEntity(world, new PrefabGUID(recipeGuid), out var prefab))
        {
            byte* data = AiRuntime.RO<RecipeData>(em, prefab, Layout.RecipeDataHideInStationOffset + 1);
            if (data != null)
            {
                duration = *(float*)(data + Layout.RecipeDataCraftDurationOffset);
                hidden = data[Layout.RecipeDataHideInStationOffset] != 0;
            }
            byte* req = AiRuntime.Buffer<RecipeRequirementBuffer>(em, prefab, RecipeBufferLayout.RequirementStride, false, out int reqLength);
            for (int i = 0; req != null && i < reqLength && i < MaxPrefabRows; i++)
            {
                RecipeBufferLayout.TryReadRequirement(req, i, out int g, out int a);
                if (g != 0) inputs.Add(new StationItem(g, a));
            }
            byte* outRaw = AiRuntime.Buffer<RecipeOutputBuffer>(em, prefab, RecipeBufferLayout.OutputStride, false, out int outLength);
            for (int i = 0; outRaw != null && i < outLength && i < MaxPrefabRows; i++)
            {
                RecipeBufferLayout.TryReadOutput(outRaw, i, out int g, out int a);
                if (g != 0) outputs.Add(new StationItem(g, a));
            }
        }
        return new StationRecipe
        {
            RecipeGuid = recipeGuid,
            Name = name,
            Kind = kind,
            Inputs = inputs,
            Outputs = outputs,
            DurationSeconds = float.IsNaN(duration) || duration < 0 ? 0 : duration,
            Enabled = enabled,
            CanToggle = canToggle,
            Unlocked = unlocked,
            HiddenInStation = hidden,
        };
    }

    private static float Duration(World world, EntityManager em, int recipeGuid)
    {
        if (recipeGuid == 0 || !PrefabLookup.TryGetEntity(world, new PrefabGUID(recipeGuid), out var prefab)) return 0;
        byte* data = AiRuntime.RO<RecipeData>(em, prefab, Layout.RecipeDataCraftDurationOffset + sizeof(float));
        if (data == null) return 0;
        float d = *(float*)(data + Layout.RecipeDataCraftDurationOffset);
        return float.IsNaN(d) || d < 0 ? 0 : d;
    }

    private static double ServerNow(EntityManager em)
    {
        if (!Singletons.TryGetEntity(em, ComponentIndex.Of<ServerTime>(ComponentType.AccessMode.ReadOnly), out var e)) return 0;
        byte* p = AiRuntime.RO<ServerTime>(em, e, Layout.ServerTimeTimeOffset + sizeof(double));
        return p == null ? 0 : *(double*)(p + Layout.ServerTimeTimeOffset);
    }

    private static StationToggleResult Refuse(int recipeGuid, bool enabledNow, string reason) => new()
    {
        Ok = false,
        Changed = false,
        Enabled = enabledNow,
        RecipeGuid = recipeGuid,
        Reason = reason,
    };

    private static EntityManager RequireServer(World world)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("ctx.Stations runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet");
        if (world.Name != Worlds.ServerName)
            throw new InvalidOperationException("ctx.Stations needs the server's default world (\"" + Worlds.ServerName + "\"), not \"" + world.Name + "\"");
        return world.EntityManager;
    }
}
