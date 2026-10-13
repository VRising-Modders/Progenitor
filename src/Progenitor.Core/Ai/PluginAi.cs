#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Threading;
using Progenitor.Ecs;
using Progenitor.Plugins;
using Progenitor.Units;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Ai;

/// <summary>
/// <see cref="PluginContext.Ai"/>: read and change boss AI on the server. Every mutation returns
/// an <see cref="IDisposable"/> that restores exactly what it changed; the context tracks each one,
/// so every unload reason restores it too. Client calls throw. See <c>docs/ai.md</c>.
/// </summary>
public sealed class PluginAi
{
    private readonly PluginContext _ctx;

    internal PluginAi(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        CastOptions = new AiCastOptions(this);
        Conditions = new AiConditions(this);
        Phases = new AiPhases(this);
        Trees = new AiTrees(this);
        Factions = new AiFactions(this);
        Variants = new AiVariants(this);
        AllySafety = new AiAllySafety(this);
    }

    /// <summary>Cast-option rows: prefab-wide edits and experimental per-instance copies.</summary>
    public AiCastOptions CastOptions { get; }

    /// <summary>Health gates in cast-option conditions (private blob copies).</summary>
    public AiConditions Conditions { get; }

    /// <summary>Per-unit phase script threshold.</summary>
    public AiPhases Phases { get; }

    /// <summary>Private behavior-tree copies applied per unit.</summary>
    public AiTrees Trees { get; }

    /// <summary>Faction masks, per-unit faction and the game's ally rule.</summary>
    public AiFactions Factions { get; }

    /// <summary>Named variants that combine the above and survive a world load through a marker buff.</summary>
    public AiVariants Variants { get; }

    /// <summary>
    /// Core's ally-safe hit default: damaging hits with no team check skip allies. Read it, or exempt
    /// or protect single prefabs (docs/ai.md, "Ally-safe damaging hits").
    /// </summary>
    public AiAllySafety AllySafety { get; }

    internal PluginContext Context => _ctx;

    /// <summary>Everything ctx.Ai knows about a boss prefab: tree, cast options, phase script, team and faction.</summary>
    public AiProfile Describe(string bossPrefab)
    {
        var world = ServerWorld(nameof(Describe));
        var em = world.EntityManager;
        var boss = AiRuntime.RequirePrefab(world, bossPrefab, "boss prefab", out int bossGuid, out string bossName);
        var profile = new AiProfile { BossName = bossName, BossGuid = bossGuid };

        int treeGuid = AiRuntime.TreeGuidOf(em, boss);
        var tree = AiRuntime.PrefabEntity(world, treeGuid);
        profile.TreeGuid = treeGuid;
        profile.TreeName = AiRuntime.Name(world, treeGuid);
        if (tree != Entity.Null)
        {
            profile.BlackboardSize = AiRuntime.BlackboardSize(em, tree);
            unsafe
            {
                byte* blob = AiRuntime.TreeBlob(em, tree);
                int length = AiRuntime.BlobLength(blob);
                if (length > 0)
                {
                    var span = new ReadOnlySpan<byte>(blob, length);
                    if (TreeBlobReader.TryNodes(span, out _, out int count)) profile.TreeNodeCount = count;
                    profile.NodeTypes = TreeBlobReader.TypeHistogram(span);
                }
            }
        }

        var slots = AiRuntime.Slots(world, boss);
        profile.Slots = slots;
        int coGuid = AiRuntime.CastOptionsGuid(em, boss);
        profile.CastOptionsGuid = coGuid;
        profile.CastOptionsName = AiRuntime.Name(world, coGuid);
        var co = AiRuntime.PrefabEntity(world, coGuid);
        if (co != Entity.Null) profile.Groups = AiRuntime.ReadCastOptions(em, co, slots, null);

        profile.Phase = AiRuntime.ReadPhase(world, boss);
        profile.UnsupportedPhaseScripts = AiRuntime.UnsupportedPhaseScripts(em, boss);
        if (AiRuntime.TryTeam(em, boss, out int team, out int faction))
        {
            profile.TeamValue = team;
            profile.TeamFactionIndex = faction;
        }
        profile.Faction = AiRuntime.FactionOfReference(world, boss);
        profile.Immortal = boss.TryRead(em, out ProjectM.Immortal immortal) && immortal.IsImmortal;
        return profile;
    }

    /// <summary>What one live unit's AI is doing now: state, target, current cast and recent casts.</summary>
    public AiObservation Observe(Entity unit)
    {
        var world = ServerWorld(nameof(Observe));
        var em = world.EntityManager;
        AiRuntime.RequireAlive(em, unit);
        int prefab = AiRuntime.PrefabGuidOf(em, unit);
        int state = AiRuntime.BehaviourState(em, unit);
        var target = AiRuntime.AggroTarget(em, unit);
        int casting = AiRuntime.CastingGroup(em, unit);
        AiRuntime.TryHealth(em, unit, out float hp, out float max, out bool dead);
        AiRuntime.TryTeam(em, unit, out int team, out int faction);
        var current = AiRuntime.CurrentTree(em, unit);
        int treeGuid = AiRuntime.TreeGuidOf(em, unit);
        var prefabTree = AiRuntime.PrefabEntity(world, treeGuid);
        int co = AiRuntime.CastOptionsGuid(em, unit);
        return new AiObservation
        {
            Unit = AiRuntime.Fmt(unit),
            Prefab = AiRuntime.Name(world, prefab),
            StateValue = state,
            State = AiNames.State(state),
            Target = target == Entity.Null ? "" : AiRuntime.Fmt(target),
            CurrentCast = casting == 0 ? "" : AiNames.Short(AiRuntime.Name(world, casting)),
            Health = hp,
            MaxHealth = max,
            IsDead = dead,
            TeamValue = team,
            FactionIndex = faction,
            Tree = AiRuntime.Name(world, treeGuid),
            TreeIsVariant = current != Entity.Null && prefabTree != Entity.Null && current != prefabTree,
            CastOptionsGuid = co,
            CastOptions = AiRuntime.Name(world, co),
            Variant = Variants.VariantOf(unit) ?? "",
            Phase = AiRuntime.ReadPhase(world, unit),
            History = AiRuntime.History(world, unit, AiRuntime.MaxHistory),
        };
    }

    internal World ServerWorld(string op)
    {
        _ctx.EnsureAttached();
        if (_ctx.IsClient) throw new InvalidOperationException("ctx.Ai." + op + " is server-only; the client has no AI to edit.");
        return AiRuntime.Server(op);
    }

    /// <summary>Wrap <paramref name="undo"/> in a tracked, idempotent handle.</summary>
    internal IDisposable Track(string what, Action undo)
    {
        var handle = new AiHandle(what, undo);
        _ctx.Track(handle);
        return handle;
    }

    internal static void Log(string line) => Plugin.Logger?.LogInfo("Ai: " + line);
}

/// <summary>An idempotent undo. Exceptions are logged, never thrown into unload.</summary>
internal sealed class AiHandle : IDisposable
{
    private readonly string _what;
    private Action? _undo;

    internal AiHandle(string what, Action undo)
    {
        _what = what;
        _undo = undo;
    }

    public void Dispose()
    {
        var undo = Interlocked.Exchange(ref _undo, null);
        if (undo == null) return;
        try { undo(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ai: undo of " + _what + " threw: " + e.Message); }
    }
}

/// <summary><see cref="PluginAi.CastOptions"/>.</summary>
public sealed class AiCastOptions
{
    private readonly PluginAi _ai;

    internal AiCastOptions(PluginAi ai) => _ai = ai;

    /// <summary>The decoded rows of a boss prefab's cast options.</summary>
    public IReadOnlyList<AiCastOptionGroup> Read(string bossPrefab)
    {
        var world = _ai.ServerWorld(nameof(Read));
        var boss = AiRuntime.RequirePrefab(world, bossPrefab, "boss prefab", out _, out _);
        var co = CastOptionsOf(world, boss, bossPrefab);
        return AiRuntime.ReadCastOptions(world.EntityManager, co, AiRuntime.Slots(world, boss), null);
    }

    /// <summary>
    /// Change every row that casts <paramref name="abilityGroup"/> (name, short name such as
    /// <c>FrostNova</c>, or GUID) on the boss's cast-option prefab. <b>Prefab-wide:</b> every unit of
    /// that boss type — already alive or spawned later — reads the new values on its next evaluation
    /// (rows are re-read live). Dispose restores the fields this call changed.
    /// </summary>
    public IDisposable Edit(string bossPrefab, string abilityGroup, CastOptionEdit edit, int? priorityGroup = null)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        edit.Validate();
        var world = _ai.ServerWorld(nameof(Edit));
        var boss = AiRuntime.RequirePrefab(world, bossPrefab, "boss prefab", out _, out string bossName);
        var co = CastOptionsOf(world, boss, bossPrefab);
        var rows = AiRuntime.SelectRows(world, co, AiRuntime.Slots(world, boss), abilityGroup, priorityGroup);
        var undo = AiRuntime.EditRows(world, rows, edit);
        PluginAi.Log("cast options of " + bossName + ": " + abilityGroup + " x" + rows.Count + " row(s) edited (prefab-wide)");
        return _ai.Track("cast-option edit " + bossName + "/" + abilityGroup, undo);
    }

    /// <summary>
    /// <b>Experimental.</b> A private copy of the boss's cast-option prefab chain under its own
    /// GUID, editable without touching the vanilla boss. Pass <paramref name="experimental"/> true to
    /// acknowledge what is unverified (docs/ai.md, "Per-instance cast options"). The copy lives for
    /// the process; units assigned to it are restored on dispose and unload.
    /// </summary>
    public CastOptionsVariant CreateVariant(string bossPrefab, string name, bool experimental)
    {
        if (!experimental)
            throw new InvalidOperationException("Per-instance cast options are experimental: pass experimental: true after reading docs/ai.md.");
        VariantBook.ValidateName(name);
        var world = _ai.ServerWorld(nameof(CreateVariant));
        var boss = AiRuntime.RequirePrefab(world, bossPrefab, "boss prefab", out int bossGuid, out string bossName);
        var co = CastOptionsOf(world, boss, bossPrefab);
        int sourceGuid = AiRuntime.CastOptionsGuid(world.EntityManager, boss);
        string id = VariantBook.MarkerId(_ai.Context.Guid, "co-" + name);
        int guid = Stunlock.Core.PrefabGUID.CalculateGuidHash(id);
        var clone = AiRuntime.CloneCastOptions(world, sourceGuid, guid);
        _ = co;
        return new CastOptionsVariant(_ai, name, bossName, bossGuid, sourceGuid, clone);
    }

    /// <summary>Point one unit at a <see cref="CastOptionsVariant"/>. Dispose restores its own cast options.</summary>
    public IDisposable Assign(Entity unit, CastOptionsVariant variant)
    {
        if (variant == null) throw new ArgumentNullException(nameof(variant));
        var world = _ai.ServerWorld(nameof(Assign));
        var em = world.EntityManager;
        AiRuntime.RequireAlive(em, unit);
        int prefab = AiRuntime.PrefabGuidOf(em, unit);
        if (prefab != variant.BossGuid)
            throw new InvalidOperationException("unit " + AiRuntime.Fmt(unit) + " is not " + variant.BossName + "; a cast-option variant casts by the boss's ability slots.");
        variant.EnsureLive(world);
        var undo = AiRuntime.AssignCastOptions(world, unit, variant.Guid);
        return _ai.Track("cast-option assign " + variant.Name, undo);
    }

    internal static Entity CastOptionsOf(World world, Entity boss, string bossPrefab)
    {
        int guid = AiRuntime.CastOptionsGuid(world.EntityManager, boss);
        var co = AiRuntime.PrefabEntity(world, guid);
        if (guid == 0 || co == Entity.Null)
            throw new InvalidOperationException("'" + bossPrefab + "' has no cast options (CastOptionsUser).");
        return co;
    }
}

/// <summary>
/// <b>Experimental.</b> A private cast-option prefab chain (<see cref="AiCastOptions.CreateVariant"/>).
/// Its rows start as a copy of the boss's and change only through this object; the vanilla boss is
/// never touched. Units use it once <see cref="AiCastOptions.Assign"/> points them at <see cref="Guid"/>.
/// </summary>
public sealed class CastOptionsVariant
{
    private readonly PluginAi _ai;
    private AiRuntime.CoClone _clone;
    private readonly List<Action<World>> _replay = new();

    internal CastOptionsVariant(PluginAi ai, string name, string bossName, int bossGuid, int sourceGuid, AiRuntime.CoClone clone)
    {
        _ai = ai;
        _clone = clone;
        Name = name;
        BossName = bossName;
        BossGuid = bossGuid;
        SourceGuid = sourceGuid;
    }

    public string Name { get; }
    public string BossName { get; }
    public int BossGuid { get; }

    /// <summary>The vanilla cast-option prefab this copy was made from.</summary>
    public int SourceGuid { get; }

    /// <summary>The copy's registered prefab GUID (what <c>CastOptionsUser</c> is set to).</summary>
    public int Guid => _clone.Guid;

    /// <summary>The copy's decoded rows.</summary>
    public IReadOnlyList<AiCastOptionGroup> Read()
    {
        var world = _ai.ServerWorld(nameof(Read));
        EnsureLive(world);
        return AiRuntime.ReadCastOptions(world.EntityManager, _clone.Root, Slots(world), null);
    }

    /// <summary>Change this copy's rows of <paramref name="abilityGroup"/>. Lasts for the copy's life.</summary>
    public CastOptionsVariant Edit(string abilityGroup, CastOptionEdit edit, int? priorityGroup = null)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        edit.Validate();
        var world = _ai.ServerWorld(nameof(Edit));
        EnsureLive(world);
        Action<World> step = w => AiRuntime.EditRows(w, AiRuntime.SelectRows(w, _clone.Root, Slots(w), abilityGroup, priorityGroup), edit);
        step(world);
        _replay.Add(step);
        return this;
    }

    /// <summary>Move this copy's health gate for <paramref name="abilityGroup"/> (a private condition blob).</summary>
    public CastOptionsVariant SetHealthGate(string abilityGroup, float fraction, int? priorityGroup = null)
    {
        AiChecks.Fraction(fraction, nameof(fraction));
        var world = _ai.ServerWorld(nameof(SetHealthGate));
        EnsureLive(world);
        Action<World> step = w => AiRuntime.SetHealthGate(w, AiRuntime.SelectRows(w, _clone.Root, Slots(w), abilityGroup, priorityGroup), fraction, abilityGroup);
        step(world);
        _replay.Add(step);
        return this;
    }

    /// <summary>After a world load the copy is rebuilt from the source and every edit is replayed.</summary>
    internal void EnsureLive(World world)
    {
        if (_clone.Epoch == WorldEpoch.Current && world.EntityManager.Exists(_clone.Root)) return;
        _clone = AiRuntime.CloneCastOptions(world, SourceGuid, _clone.Guid);
        foreach (var step in _replay) step(world);
    }

    private List<AiAbilitySlot> Slots(World world)
    {
        var boss = AiRuntime.PrefabEntity(world, BossGuid);
        return AiRuntime.Slots(world, boss);
    }
}

/// <summary><see cref="PluginAi.Conditions"/>.</summary>
public sealed class AiConditions
{
    private readonly PluginAi _ai;

    internal AiConditions(PluginAi ai) => _ai = ai;

    /// <summary>
    /// Make the rows of <paramref name="abilityGroup"/> require boss health at or below
    /// <paramref name="fraction"/> (1 = from full health). Each row is pointed at a private copy of
    /// its condition blob; the shared original is never written. Prefab-wide, like
    /// <see cref="AiCastOptions.Edit"/>. Refused when no selected row has a HealthFactor gate on the
    /// boss. Dispose points the rows back (the copies stay allocated).
    /// </summary>
    public IDisposable SetHealthGate(string bossPrefab, string abilityGroup, float fraction, int? priorityGroup = null)
    {
        AiChecks.Fraction(fraction, nameof(fraction));
        var world = _ai.ServerWorld(nameof(SetHealthGate));
        var boss = AiRuntime.RequirePrefab(world, bossPrefab, "boss prefab", out _, out string bossName);
        var co = AiCastOptions.CastOptionsOf(world, boss, bossPrefab);
        var rows = AiRuntime.SelectRows(world, co, AiRuntime.Slots(world, boss), abilityGroup, priorityGroup);
        var undo = AiRuntime.SetHealthGate(world, rows, fraction, abilityGroup);
        PluginAi.Log("health gate of " + bossName + "/" + abilityGroup + " -> " + fraction + " (prefab-wide)");
        return _ai.Track("health gate " + bossName + "/" + abilityGroup, undo);
    }
}

/// <summary><see cref="PluginAi.Phases"/>.</summary>
public sealed class AiPhases
{
    private readonly PluginAi _ai;

    internal AiPhases(PluginAi ai) => _ai = ai;

    /// <summary>The unit's <c>Script_ApplyBuffUnderHealthThreshold</c>, or null when it has none.</summary>
    public AiPhaseScript? Get(Entity unit)
    {
        var world = _ai.ServerWorld(nameof(Get));
        AiRuntime.RequireAlive(world.EntityManager, unit);
        return AiRuntime.ReadPhase(world, unit);
    }

    /// <summary>
    /// Per instance: fire the phase buff when this unit's health drops to <paramref name="fraction"/>
    /// (optionally a different buff, by name or GUID). Re-arms when the unit is above the new
    /// threshold. Frostmaw's phase buff is its downed buff, so a higher threshold defeats it earlier.
    /// Dispose restores the threshold and buff; a buff that already fired stays fired.
    /// </summary>
    public IDisposable SetThreshold(Entity unit, float fraction, string? buff = null)
    {
        AiChecks.Fraction(fraction, nameof(fraction));
        var world = _ai.ServerWorld(nameof(SetThreshold));
        int? buffGuid = null;
        if (!string.IsNullOrWhiteSpace(buff))
        {
            AiRuntime.RequirePrefab(world, buff!, "buff", out int g, out _);
            buffGuid = g;
        }
        if (AiRuntime.ReadPhase(world, unit) == null)
            throw new InvalidOperationException("unit " + AiRuntime.Fmt(unit) + " has no Script_ApplyBuffUnderHealthThreshold; other phase scripts are not supported yet.");
        var undo = AiRuntime.SetPhase(world, unit, fraction, buffGuid);
        return _ai.Track("phase threshold " + AiRuntime.Fmt(unit), undo);
    }
}

/// <summary><see cref="PluginAi.Trees"/>.</summary>
public sealed class AiTrees
{
    private readonly PluginAi _ai;
    private readonly Dictionary<TreeVariant, (int Epoch, Entity Tree)> _live = new();

    internal AiTrees(PluginAi ai) => _ai = ai;

    /// <summary>An editable private copy of the boss's behavior tree. Nothing changes until <see cref="Apply"/>.</summary>
    public TreeVariant CreateVariant(string bossPrefab)
    {
        var world = _ai.ServerWorld(nameof(CreateVariant));
        var em = world.EntityManager;
        var boss = AiRuntime.RequirePrefab(world, bossPrefab, "boss prefab", out int bossGuid, out string bossName);
        int treeGuid = AiRuntime.TreeGuidOf(em, boss);
        var tree = AiRuntime.PrefabEntity(world, treeGuid);
        if (tree == Entity.Null) throw new InvalidOperationException("'" + bossPrefab + "' has no behavior tree binding.");
        byte[] data;
        unsafe
        {
            byte* blob = AiRuntime.TreeBlob(em, tree);
            if (blob == null) throw new InvalidOperationException("the tree prefab has no blob.");
            data = AiRuntime.CopyOut(blob);
        }
        return new TreeVariant(bossName, bossGuid, AiRuntime.Name(world, treeGuid), treeGuid, data);
    }

    /// <summary>
    /// Run <paramref name="variant"/> on one unit. The unit's <c>ExpectedTreeEntity</c> is pointed at
    /// a private tree entity; the tree job then aborts the running nodes against the old blob and
    /// restarts on the copy (safe mid-fight, live-verified). The first Apply freezes the variant.
    /// Refused when the unit binds another tree. Dispose moves the unit back to the tree it had.
    /// <b>Experimental</b> (<paramref name="experimental"/> must be true): the private tree
    /// reference is not saved, so a unit saved on a variant reloads with a null tree (live), which
    /// Core's post-load repair points back at the prefab tree. Revert or remove variant units
    /// before saving.
    /// </summary>
    public IDisposable Apply(Entity unit, TreeVariant variant, bool experimental)
    {
        if (variant == null) throw new ArgumentNullException(nameof(variant));
        if (!experimental)
            throw new InvalidOperationException("Tree variants are experimental: pass experimental: true after reading docs/ai.md (a private tree is not saved).");
        var world = _ai.ServerWorld(nameof(Apply));
        return _ai.Track("tree variant " + AiRuntime.Fmt(unit), ApplyCore(world, unit, variant));
    }

    internal Action ApplyCore(World world, Entity unit, TreeVariant variant)
    {
        var em = world.EntityManager;
        AiRuntime.RequireAlive(em, unit);
        int bound = AiRuntime.TreeGuidOf(em, unit);
        if (bound != variant.TreeGuid)
            throw new InvalidOperationException("unit " + AiRuntime.Fmt(unit) + " binds tree " + bound + ", not " + variant.TreeName + "; a tree variant only runs on units of its own tree.");
        // BehaviourTreeBindingSystem_Spawn resolves the binding after the spawn tick and would
        // overwrite ExpectedTreeEntity; apply once the unit runs a tree.
        if (AiRuntime.CurrentTree(em, unit) == Entity.Null)
            throw new InvalidOperationException("unit " + AiRuntime.Fmt(unit) + " has no running tree yet (spawned this frame?); apply the variant a moment after spawning.");
        var tree = Live(world, variant);
        var previous = AiRuntime.ExpectedTree(em, unit);
        AiRuntime.WriteExpectedTree(em, unit, tree);
        int epoch = WorldEpoch.Current;
        return () =>
        {
            if (WorldEpoch.IsStale(epoch)) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated || !w.EntityManager.Exists(unit)) return;
            if (AiRuntime.ExpectedTree(w.EntityManager, unit) != tree) return;
            var back = previous != Entity.Null && w.EntityManager.Exists(previous) ? previous : AiRuntime.PrefabEntity(w, variant.TreeGuid);
            if (back != Entity.Null) AiRuntime.WriteExpectedTree(w.EntityManager, unit, back);
        };
    }

    /// <summary>Point the unit back at its prefab's own tree entity. False when it has no tree binding.</summary>
    public bool Revert(Entity unit)
    {
        var world = _ai.ServerWorld(nameof(Revert));
        var em = world.EntityManager;
        AiRuntime.RequireAlive(em, unit);
        var tree = AiRuntime.PrefabEntity(world, AiRuntime.TreeGuidOf(em, unit));
        if (tree == Entity.Null) return false;
        AiRuntime.WriteExpectedTree(em, unit, tree);
        return true;
    }

    internal Entity Live(World world, TreeVariant variant)
    {
        if (_live.TryGetValue(variant, out var entry) && entry.Epoch == WorldEpoch.Current && world.EntityManager.Exists(entry.Tree))
            return entry.Tree;
        var tree = AiRuntime.MaterializeTree(world, variant);
        _live[variant] = (WorldEpoch.Current, tree);
        return tree;
    }
}

/// <summary><see cref="PluginAi.Factions"/>.</summary>
public sealed class AiFactions
{
    private readonly PluginAi _ai;

    internal AiFactions(PluginAi ai) => _ai = ai;

    /// <summary>A faction prefab's index and relationship masks (for example <c>Faction_Bandits</c>).</summary>
    public AiFactionInfo Describe(string faction) => AiRuntime.RequireFaction(_ai.ServerWorld(nameof(Describe)), faction);

    /// <summary>The faction behind the unit's current <c>Team.FactionIndex</c>, or null when unknown.</summary>
    public AiFactionInfo? Of(Entity unit)
    {
        var world = _ai.ServerWorld(nameof(Of));
        if (!AiRuntime.TryTeam(world.EntityManager, unit, out _, out int index)) return null;
        return AiRuntime.Factions(world).TryGetValue(index, out var info) ? info : null;
    }

    /// <summary>
    /// The game's <c>Team.IsAllies(a, b)</c>: whether <paramref name="a"/> treats <paramref name="b"/>
    /// as an ally (not symmetric). Null when either unit has no <c>Team</c> or a faction index is
    /// unknown. Ability hit conditions such as Frostmaw's melee use <c>NOT IsAlly(Target)</c>.
    /// </summary>
    public bool? AreAllies(Entity a, Entity b)
    {
        var world = _ai.ServerWorld(nameof(AreAllies));
        var em = world.EntityManager;
        if (!AiRuntime.TryTeam(em, a, out int va, out int fa) || !AiRuntime.TryTeam(em, b, out int vb, out int fb)) return null;
        var table = AiRuntime.Factions(world);
        return FactionMath.IsAllies(va, fa, vb, fb, index =>
            table.TryGetValue(index, out var info) ? (info.Membership, info.Friendly) : null);
    }

    /// <summary>
    /// Put one unit on <paramref name="faction"/>: writes its <c>FactionReference</c> (the source the
    /// game derives <c>Team.FactionIndex</c> from at spawn) and its <c>Team.FactionIndex</c> (what ally
    /// checks read). Dispose restores both.
    /// </summary>
    public IDisposable Set(Entity unit, string faction)
    {
        var world = _ai.ServerWorld(nameof(Set));
        var info = AiRuntime.RequireFaction(world, faction);
        var undo = AiRuntime.SetFaction(world, unit, info);
        return _ai.Track("faction " + AiRuntime.Fmt(unit), undo);
    }

    /// <summary>Untracked write for units the plugin despawns itself (<c>ctx.Units</c>).</summary>
    internal AiFactionInfo SetUntracked(Entity unit, string faction)
    {
        var world = _ai.ServerWorld(nameof(Set));
        var info = AiRuntime.RequireFaction(world, faction);
        AiRuntime.SetFaction(world, unit, info);
        return info;
    }
}
#endif
