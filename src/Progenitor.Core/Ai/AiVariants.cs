#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Behaviours;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Ai;

/// <summary>
/// <see cref="PluginAi.Variants"/>: named per-unit AI variants that survive a world load.
///
/// <para><see cref="Define"/> registers the variant and a plugin-owned marker buff (a
/// <c>DefineBuff</c> clone, one per variant). Core itself strips the server clone to an infinite,
/// effect-free tag — no <c>Content.Edit</c>, so no <c>[NetworkCompatibility]</c> is needed — and
/// <see cref="Apply"/> refuses while it cannot verify that. <see cref="Apply"/> changes one unit
/// and puts the marker on it. After a world load every unit that carries a marker gets its variant
/// again; vanilla units never carry one. Whether the marker survives a save and restart on an NPC
/// is not verified yet (docs/ai.md, "Persistence").</para>
///
/// <para>Dispose of the returned <see cref="AiVariantHandle"/> reverts the unit and removes the
/// marker. Plugin unload reverts the unit and <b>keeps</b> the marker, so the next generation (or
/// the next boot) finds it.</para>
/// </summary>
public sealed class AiVariants
{
    /// <summary>
    /// Marker sources Core knows how to make inert (the component set <see cref="StripMarker"/>
    /// removes was checked on this source; it is the Sample no-drain buff's source).
    /// </summary>
    internal static readonly string[] InertSources = { InertCloneRules.MarkerSource };

    private static readonly double[] RescanDelays = { 3.0, 12.0, 32.0 };

    private readonly PluginAi _ai;
    private readonly VariantBook _book = new();
    private readonly Dictionary<string, VariantSpec> _specs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeVariant> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CastOptionsVariant> _castOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiVariantHandle> _applications = new(StringComparer.Ordinal);
    private bool _hooked;

    internal AiVariants(PluginAi ai) => _ai = ai;

    /// <summary>
    /// Register <paramref name="name"/> (letters, digits, <c>_</c>, <c>-</c>). Safe in <c>OnLoad</c>
    /// before the world exists: the marker clone and the boss lookup wait for it. Server only.
    /// Refused when the spec uses tree or cast-option edits without
    /// <see cref="VariantSpec.AllowExperimental"/>, or names a marker source Core cannot make inert.
    /// </summary>
    public void Define(string name, VariantSpec spec)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        var ctx = _ai.Context;
        ctx.EnsureAttached();
        if (ctx.IsClient) throw new InvalidOperationException("ctx.Ai.Variants.Define is server-only.");
        if ((spec.UsesCastOptions || spec.Tree != null) && !spec.AllowExperimental)
            throw new InvalidOperationException("variant '" + name + "' uses per-instance tree or cast-option edits, which are experimental; set AllowExperimental after reading docs/ai.md.");
        if (Array.FindIndex(InertSources, s => string.Equals(s, spec.MarkerSource, StringComparison.Ordinal)) < 0)
            throw new InvalidOperationException("variant '" + name + "': marker source '" + spec.MarkerSource + "' is not one Core can make inert (" + string.Join(", ", InertSources) + ").");
        string markerId = VariantBook.MarkerId(ctx.Guid, name);
        int markerGuid = PrefabGUID.CalculateGuidHash(markerId);
        _book.Define(name, 0, markerGuid, markerId);
        _specs[name] = spec;
        // Server-authoritative: Progenitor clients strip their copy of this clone only because the
        // server says Core strips it (InertClones), never from the id's shape. Announced before
        // Register: reliable events keep order, so the client knows to strip before PrfD builds it.
        InertClones.Announce(markerGuid, InertCloneRole.Marker);
        ctx.Content.DefineBuff(markerId).CloneOf(spec.MarkerSource).Register();
        ctx.Track(new ActionDisposable(() =>
        {
            _book.Remove(name);
            _specs.Remove(name);
            InertClones.Forget(markerGuid);
        }));
        Hook();
        var world = Worlds.Server;
        if (world != null && world.IsCreated && PrefabLookup.TryGetEntity(world, new PrefabGUID(markerGuid), out var marker) && marker != Entity.Null)
            MakeInert(world, markerId, marker);
        PluginAi.Log("variant '" + name + "' defined for " + spec.BossPrefab + " (marker " + markerId + " = " + markerGuid + ")");
    }

    /// <summary>
    /// Apply variant <paramref name="name"/> to one unit of its boss prefab and tag it with the
    /// variant's marker buff. Refused (nothing changed) when the marker clone is not registered yet or
    /// cannot be made inert. The handle reports whether the marker landed; dispose reverts every
    /// change and removes the marker.
    /// </summary>
    public AiVariantHandle Apply(Entity unit, string name)
    {
        var world = _ai.ServerWorld(nameof(Apply));
        if (!_book.TryGet(name, out var entry)) throw new InvalidOperationException("no variant named '" + name + "' is defined.");
        var app = ApplyCore(world, unit, entry, restored: false);
        _ai.Context.Track(new ActionDisposable(app.ReleaseForUnload));
        return app;
    }

    /// <summary>Revert <paramref name="unit"/>'s variant and remove its marker. False when it has none.</summary>
    public bool Remove(Entity unit)
    {
        _ai.ServerWorld(nameof(Remove));
        if (!_applications.TryGetValue(Key(unit), out var app)) return false;
        app.Dispose();
        return true;
    }

    /// <summary>The variant applied to <paramref name="unit"/> in the current world, or null.</summary>
    public string? VariantOf(Entity unit) => _book.VariantOf(Key(unit));

    /// <summary>Live units that carry <paramref name="name"/> in the current world.</summary>
    public IReadOnlyList<Entity> UnitsOf(string name)
    {
        var list = new List<Entity>();
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return list;
        foreach (var pair in _book.AppliedIn(WorldEpoch.Current))
        {
            if (!string.Equals(pair.Value, name, StringComparison.Ordinal)) continue;
            if (_applications.TryGetValue(pair.Key, out var app) && world.EntityManager.Exists(app.Unit)) list.Add(app.Unit);
        }
        return list;
    }

    /// <summary>
    /// Find every unit that carries one of this plugin's markers and is not yet re-applied in this
    /// world, and apply its variant again. Runs by itself after a world load; returns how many units
    /// it re-applied.
    /// </summary>
    public int Reapply() => Rescan("manual");

    // ---- internals ------------------------------------------------------------------------------

    private static string Key(Entity unit) => VariantBook.Key(WorldEpoch.Current, unit.Index, unit.Version);

    private AiVariantHandle ApplyCore(World world, Entity unit, VariantBook.Entry entry, bool restored)
    {
        var em = world.EntityManager;
        AiRuntime.RequireAlive(em, unit);
        var spec = _specs[entry.Name];
        ResolveBoss(world, entry, spec);
        int prefab = AiRuntime.PrefabGuidOf(em, unit);
        if (prefab != entry.BossGuid)
            throw new InvalidOperationException("unit " + AiRuntime.Fmt(unit) + " is not " + spec.BossPrefab + "; variant '" + entry.Name + "' cannot apply.");
        string key = Key(unit);
        if (_applications.TryGetValue(key, out var existing) && !existing.Ended)
            throw new InvalidOperationException("unit " + AiRuntime.Fmt(unit) + " already carries variant '" + existing.Variant + "'; remove it first.");
        if (!restored) EnsureMarkerInert(world, entry);

        // A unit restored from a save already holds the variant's values; its undo must restore the
        // prefab's, not what it finds.
        var originals = restored ? AiRuntime.PrefabEntity(world, entry.BossGuid) : Entity.Null;
        var undos = new List<Action>();
        try
        {
            if (spec.Tree != null)
            {
                if (!_trees.TryGetValue(entry.Name, out var tree))
                {
                    tree = _ai.Trees.CreateVariant(spec.BossPrefab);
                    spec.Tree(tree);
                    _trees[entry.Name] = tree;
                }
                undos.Add(_ai.Trees.ApplyCore(world, unit, tree));
            }
            if (spec.UsesCastOptions)
            {
                if (!_castOptions.TryGetValue(entry.Name, out var co))
                {
                    co = _ai.CastOptions.CreateVariant(spec.BossPrefab, entry.Name, experimental: true);
                    foreach (var edit in spec.CastEdits) co.Edit(edit.Key, edit.Value);
                    foreach (var gate in spec.HealthGates) co.SetHealthGate(gate.Key, gate.Value);
                    _castOptions[entry.Name] = co;
                }
                co.EnsureLive(world);
                undos.Add(AiRuntime.AssignCastOptions(world, unit, co.Guid, originals));
            }
            if (spec.PhaseThreshold is float threshold)
            {
                if (AiRuntime.ReadPhase(world, unit) != null)
                    undos.Add(AiRuntime.SetPhase(world, unit, threshold, null, originals));
                else
                    PluginAi.Log("variant '" + entry.Name + "': " + AiRuntime.Fmt(unit) + " has no supported phase script; threshold skipped");
            }
            if (!string.IsNullOrWhiteSpace(spec.Faction))
            {
                var faction = AiRuntime.RequireFaction(world, spec.Faction!);
                undos.Add(AiRuntime.SetFaction(world, unit, faction, originals));
            }
        }
        catch
        {
            for (int i = undos.Count - 1; i >= 0; i--)
            {
                try { undos[i](); } catch { }
            }
            throw;
        }

        var app = new AiVariantHandle(this, entry.Name, unit, entry.MarkerGuid, key, undos);
        _applications[key] = app;
        _book.Record(key, entry.Name);
        if (restored)
            app.MarkerFound();
        else
            PutMarker(world, app);
        PluginAi.Log("variant '" + entry.Name + "' on " + AiRuntime.Fmt(unit) + (restored ? " (re-applied from its marker; undo restores the prefab's values)" : ""));
        return app;
    }

    /// <summary>
    /// <c>DebugEventsSystem.ApplyBuff</c> spawns the buff synchronously on <c>FromCharacter.Character</c>
    /// (native: <c>BuffUtility.TrySpawnBuff</c> then playback) — the unit here. The instance is
    /// looked for now and again a second later; a missing marker is a warning and is reported on the
    /// handle, never silent.
    /// </summary>
    private static void PutMarker(World world, AiVariantHandle app)
    {
        try
        {
            AiRuntime.ApplyBuff(world, app.Unit, app.MarkerGuid);
        }
        catch (Exception e)
        {
            app.MarkerMissing("ApplyBuff threw: " + e.Message);
            return;
        }
        if (AiRuntime.FindBuff(world.EntityManager, app.Unit, app.MarkerGuid) != Entity.Null)
        {
            app.MarkerFound();
            return;
        }
        int epoch = WorldEpoch.Current;
        Dev.MainThread.PostDelayed(1.0, () =>
        {
            if (WorldEpoch.IsStale(epoch) || app.Ended) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated || !w.EntityManager.Exists(app.Unit)) return;
            if (AiRuntime.FindBuff(w.EntityManager, app.Unit, app.MarkerGuid) != Entity.Null) app.MarkerFound();
            else app.MarkerMissing("the marker buff is not in the unit's BuffBuffer one second after ApplyBuff");
        });
    }

    private static void ResolveBoss(World world, VariantBook.Entry entry, VariantSpec spec)
    {
        if (entry.BossGuid != 0) return;
        AiRuntime.RequirePrefab(world, spec.BossPrefab, "boss prefab", out int guid, out _);
        entry.BossGuid = guid;
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        var ctx = _ai.Context;
        // One anchor per world load: WorldLoaded (published after the epoch moved). The immediate
        // pass covers a Define into a world that is already up (plugin or Core reload).
        ctx.Events.On<WorldLoaded>(_ => Schedule("world load"));
        var world = Worlds.Server;
        if (world != null && world.IsCreated) Schedule("define");
    }

    private int _scheduledEpoch = int.MinValue;

    /// <summary>
    /// Scan after Core's tree repair (2, 10, 30 s) has had its turn: a restored unit whose tree
    /// reference came back null is refused until the repair points it at the prefab tree.
    /// </summary>
    private void Schedule(string anchor)
    {
        int epoch = WorldEpoch.Current;
        if (_scheduledEpoch == epoch) return; // already scheduled for this world
        _scheduledEpoch = epoch;
        var owner = _ai.Context.Owner;
        foreach (double delay in RescanDelays)
        {
            double d = delay;
            Dev.MainThread.PostDelayed(d, () =>
            {
                if (WorldEpoch.IsStale(epoch)) return;
                Rescan(anchor + " +" + d + " s");
            }, owner);
        }
    }

    private int Rescan(string why)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || _book.Count == 0) return 0;
        var em = world.EntityManager;
        int epoch = WorldEpoch.Current;
        _book.DropStale(epoch);
        foreach (var entry in _book.Entries)
        {
            try { ResolveBoss(world, entry, _specs[entry.Name]); }
            catch (Exception e) { PluginAi.Log("variant '" + entry.Name + "': " + e.Message); }
        }

        var scan = new List<VariantBook.ScannedUnit>();
        var units = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<BehaviourTreeInstance>(ComponentType.AccessMode.ReadOnly));
        foreach (var unit in units)
        {
            var buffs = AiRuntime.BuffGuids(em, unit);
            bool marked = false;
            foreach (int guid in buffs)
                if (_book.TryByMarker(guid, out _)) { marked = true; break; }
            if (marked) scan.Add(new VariantBook.ScannedUnit(unit.Index, unit.Version, AiRuntime.PrefabGuidOf(em, unit), buffs));
        }

        var mismatched = new List<string>();
        int applied = 0;
        foreach (var (found, entry) in _book.PlanReapply(scan, epoch, mismatched))
        {
            var unit = new Entity { Index = found.Index, Version = found.Version };
            try
            {
                var app = ApplyCore(world, unit, entry, restored: true);
                _ai.Context.Track(new ActionDisposable(app.ReleaseForUnload));
                applied++;
            }
            catch (Exception e)
            {
                PluginAi.Log("variant '" + entry.Name + "' re-apply on " + AiRuntime.Fmt(unit) + " refused (" + why + "): " + e.Message);
            }
        }
        foreach (var line in mismatched) PluginAi.Log(line);
        if (applied > 0 || scan.Count > 0)
            PluginAi.Log("variants rescan (" + why + "): " + scan.Count + " marked unit(s), " + applied + " re-applied");
        return applied;
    }

    // ---- the marker ----------------------------------------------------------------------------

    /// <summary>
    /// The marker's server clone must be inert before any unit wears it: otherwise it is the source
    /// buff (live: a refused Content.Edit left the Haste clone's speed and its short LifeTime, so
    /// the marker expired off the unit). Core strips it directly, then checks.
    /// </summary>
    private static void EnsureMarkerInert(World world, VariantBook.Entry entry)
    {
        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(entry.MarkerGuid), out var marker) || marker == Entity.Null)
            throw new InvalidOperationException("variant '" + entry.Name + "': marker buff " + entry.MarkerId + " (" + entry.MarkerGuid + ") is not registered yet (DefineBuff clones apply after the save loads); refusing to apply the variant without its marker.");
        MakeInert(world, entry.MarkerId, marker);
    }

    /// <summary>Strip <paramref name="marker"/> to an inert tag and check it; throws when it is not. Also used by <see cref="ApiSpawnMarker"/>.</summary>
    internal static void MakeInert(World world, string markerId, Entity marker)
    {
        var em = world.EntityManager;
        StripMarker(em, marker);
        var problems = MarkerProblems(em, marker);
        if (problems.Count > 0)
        {
            string text = "Ai: marker " + markerId + " could not be made inert: " + string.Join(", ", problems);
            Plugin.Logger?.LogError(text);
            throw new InvalidOperationException(text);
        }
    }

    /// <summary>
    /// Server-side, Core-owned edit of the marker clone: infinite life, persists through death,
    /// and none of the source's gameplay (spawn events, movement speed) or visuals. Structural,
    /// on the clone prefab only, never restored (the clone lives for the process). Clients build
    /// their own copy from the definition packet; <see cref="InertClones"/> runs this same strip on
    /// it (plus the buff-bar entry), so markers show none of the source's visuals.
    /// </summary>
    private static void StripMarker(EntityManager em, Entity e)
    {
        var life = new LifeTime { Duration = -1f, EndAction = LifeTimeEndAction.None };
        if (!e.Has<LifeTime>(em)) e.Add<LifeTime>(em);
        e.Write(em, in life);
        if (!e.Has<Buff_Persists_Through_Death>(em)) e.Add<Buff_Persists_Through_Death>(em);
        bool prefab = e.Has<Unity.Entities.Prefab>(em);
        foreach (string name in InertCloneRules.ServerStrip)
        {
            if (!prefab && Array.IndexOf(InertCloneRules.PrefabOnlyStrip, name) >= 0) continue;
            if (!StripHandlers.TryGetValue(name, out var handler))
            {
                Plugin.Logger?.LogError("Ai: marker strip list names " + name + ", which has no handler.");
                continue;
            }
            try { if (handler.Has(em, e)) handler.Remove(em, e); }
            catch (Exception ex) { Plugin.Logger?.LogWarning("Ai: marker strip " + name + " threw: " + ex.Message); }
        }
    }

    /// <summary>
    /// Typed has/remove for every name in <see cref="InertCloneRules.ServerStrip"/>. The list is the
    /// contract (and what the tests read); this table only turns a name into the non-generic
    /// <c>AddComponent</c>/<c>RemoveComponent</c> calls <see cref="EntityExtensions"/> makes.
    /// </summary>
    private static readonly Dictionary<string, (Func<EntityManager, Entity, bool> Has, Action<EntityManager, Entity> Remove)> StripHandlers =
        new(StringComparer.Ordinal)
        {
            ["CreateGameplayEventsOnSpawn"] = Handler<CreateGameplayEventsOnSpawn>(),
            ["GameplayEventListeners"] = Handler<GameplayEventListeners>(),
            ["RemoveBuffOnGameplayEvent"] = Handler<RemoveBuffOnGameplayEvent>(),
            ["RemoveBuffOnGameplayEventEntry"] = Handler<RemoveBuffOnGameplayEventEntry>(),
            ["ModifyMovementSpeedBuff"] = Handler<ModifyMovementSpeedBuff>(),
            ["ModifyMovementSpeedBuffModification"] = Handler<ModifyMovementSpeedBuffModification>(),
            ["BuffSequence"] = Handler<BuffSequence>(),
            ["Sequencer.BuffSequenceSpawned"] = Handler<ProjectM.Sequencer.BuffSequenceSpawned>(),
            ["Sequencer.SpawnSequenceForEntity"] = Handler<ProjectM.Sequencer.SpawnSequenceForEntity>(),
            ["SequenceGUID"] = Handler<SequenceGUID>(),
        };

    private static (Func<EntityManager, Entity, bool>, Action<EntityManager, Entity>) Handler<T>() =>
        ((em, e) => e.Has<T>(em), (em, e) => e.Remove<T>(em));

    /// <summary>
    /// What stops <paramref name="e"/> being an inert tag: a bad <c>LifeTime</c>, no death persistence, or
    /// any <see cref="InertCloneRules.ServerStrip"/> component still present (on an instance, the
    /// prefab-only sequence buffers other than <c>BuffSequence</c> are not checked).
    /// </summary>
    private static List<string> MarkerProblems(EntityManager em, Entity e)
    {
        var problems = new List<string>();
        if (!e.TryRead(em, out LifeTime life)) problems.Add("no LifeTime");
        else if (!(life.Duration < 0f) || life.EndAction != LifeTimeEndAction.None) problems.Add("LifeTime is " + life.Duration + "/" + life.EndAction);
        if (!e.Has<Buff_Persists_Through_Death>(em)) problems.Add("no Buff_Persists_Through_Death");
        bool prefab = e.Has<Unity.Entities.Prefab>(em);
        foreach (string name in InertCloneRules.ServerStrip)
        {
            if (!prefab && name != "BuffSequence" && Array.IndexOf(InertCloneRules.PrefabOnlyStrip, name) >= 0) continue;
            if (!StripHandlers.TryGetValue(name, out var handler)) problems.Add("no handler for " + name);
            else if (handler.Has(em, e)) problems.Add("still has " + name);
        }
        return problems;
    }

    internal void Forget(AiVariantHandle app)
    {
        if (_applications.TryGetValue(app.Key, out var current) && ReferenceEquals(current, app))
        {
            _applications.Remove(app.Key);
            _book.Forget(app.Key);
        }
    }
}

/// <summary>
/// One unit's variant (<see cref="AiVariants.Apply"/>). <see cref="MarkerApplied"/> says whether
/// the persistence marker is on the unit; <see cref="MarkerProblem"/> says why not. Dispose: full
/// revert and marker removal. Plugin unload: revert, keep the marker.
/// </summary>
public sealed class AiVariantHandle : IDisposable
{
    private readonly AiVariants _owner;
    private readonly List<Action> _undos;
    private readonly int _epoch;

    internal AiVariantHandle(AiVariants owner, string variant, Entity unit, int markerGuid, string key, List<Action> undos)
    {
        _owner = owner;
        Variant = variant;
        Unit = unit;
        MarkerGuid = markerGuid;
        Key = key;
        _undos = undos;
        _epoch = WorldEpoch.Current;
    }

    /// <summary>The variant's name.</summary>
    public string Variant { get; }

    /// <summary>The unit, valid in the world epoch it was applied in.</summary>
    public Entity Unit { get; }

    /// <summary>True once the marker buff was seen on the unit. False while pending or after a failure.</summary>
    public bool MarkerApplied { get; private set; }

    /// <summary>Why the marker is not on the unit, or null. Without it the variant does not survive a load.</summary>
    public string? MarkerProblem { get; private set; }

    internal int MarkerGuid { get; }
    internal string Key { get; }
    internal bool Ended { get; private set; }

    internal void MarkerFound()
    {
        MarkerApplied = true;
        MarkerProblem = null;
    }

    internal void MarkerMissing(string why)
    {
        MarkerApplied = false;
        MarkerProblem = why;
        Plugin.Logger?.LogWarning("Ai: variant '" + Variant + "' on " + AiRuntime.Fmt(Unit) + ": marker not applied: " + why + ". The variant works now but will not be re-applied after a load.");
    }

    public void Dispose() => End(removeMarker: true);

    internal void ReleaseForUnload() => End(removeMarker: false);

    private void End(bool removeMarker)
    {
        if (Ended) return;
        Ended = true;
        for (int i = _undos.Count - 1; i >= 0; i--)
        {
            try { _undos[i](); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ai: variant '" + Variant + "' undo threw: " + e.Message); }
        }
        if (removeMarker && !WorldEpoch.IsStale(_epoch))
        {
            var world = Worlds.Server;
            try
            {
                if (world != null && world.IsCreated && world.EntityManager.Exists(Unit))
                    AiRuntime.RemoveBuff(world, Unit, MarkerGuid);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ai: variant '" + Variant + "' marker removal threw: " + e.Message); }
        }
        _owner.Forget(this);
    }
}
#endif
