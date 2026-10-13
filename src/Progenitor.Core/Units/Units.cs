using System;
using System.Collections.Generic;
using System.Globalization;
#if !PROGENITOR_TESTS
using Progenitor.Ecs;
using Unity.Entities;
#endif

namespace Progenitor.Units;

/// <summary>
/// Server-side unit spawn and steering. Spawn instantiates a vanilla prefab and places it
/// through UnitSpawnPlacement (Translation + LastTranslation + SpawnTransform;
/// evidence §11 / 108). The Dev <c>spawn</c> op uses the same helper. Follow is a Follower overlay plus a Follow
/// behavior-tree request (evidence 33); prefabs that lack Follower get the component added
/// and removed on GoTo / Despawn. GoTo writes <c>AiMove_Server</c> the way
/// ApproachPosition.Tick does (evidence 46). Never a bare marker entity (evidence 32) and
/// never a raw PathRequest (evidence §11). Every call is safe on the client: it returns a
/// disabled handle and one Debug line so a shared plugin does not need a side check.
/// Reload-safe: handles are <see cref="IDisposable"/> and <see cref="RemoveAll"/> despawns
/// what that plugin spawned. Plugin unload calls <see cref="SweepForUnload"/> from
/// <c>SweepRegistrations</c> so a static-API spawn cannot pin the plugin ALC. A sequence also
/// holds your <see cref="UnitStep.Then"/> callbacks while it plays, so pass <c>owner</c> to
/// Spawn (or use <c>PluginContext.Units</c>, which does it for you); a handle somebody else
/// owns whose sequence carries a callback from the unloading assembly keeps playing with those
/// <c>Then</c> steps turned into no-ops, and a handle the unloading plugin owns has its
/// sequence stopped and reported — either way the unit itself stays until Core uninstall.
/// <b>What counts as yours:</b> a handle spawned with your plugin instance (or any owner object
/// of a type your assembly declares) <i>or</i> with your <c>pluginId</c>. A bare
/// <c>new object()</c> token is declared in the BCL, so if that is your <c>owner</c>, pass
/// <c>pluginId</c> too or the sweep cannot tell your unit from a stranger's (R58 finding 3).
/// Spawn / Follow / GoTo / Despawn / RemoveAll throw
/// <see cref="InvalidOperationException"/> off the game main thread. Effects does not
/// marshal off-thread either (client GameObject calls run on the caller); Units throws
/// because EntityManager writes crash natively from a worker. Call from
/// <c>Plugin.Frame</c>, a command handler, or EventBus.
/// </summary>
public static class Units
{
    /// <summary>
    /// SkipCoreModules token. Same string as <see cref="HarmonySlotHandoff.UnitsModule"/>.
    /// </summary>
    public const string ModuleName = HarmonySlotHandoff.UnitsModule;

    private static readonly object Gate = new();
    private static readonly List<UnitHandle> Live = new();
    private static readonly List<UnitHandle> PollBuffer = new();
    private static IUnitRuntime? _runtime;
    private static bool _installed;
    private static bool _skipped;
    private static bool _frameHooked;

    /// <summary>
    /// Instantiate <paramref name="prefab"/> (spawnable name, or a signed GUID hash string)
    /// at <paramref name="position"/>. Pass <paramref name="owner"/> (the BepInEx plugin)
    /// so unload can sweep; <c>PluginContext.Units</c> fills owner + plugin id and
    /// <c>Track</c>s the handle.
    /// </summary>
    /// <param name="behaviourTree">
    /// Optional vanilla behavior-tree prefab (name such as <c>BEH_Farmlands_Animal_Standard</c>,
    /// or a signed GUID hash) written to <c>BehaviourTreeBinding.PrefabGUID</c> on the spawn
    /// tick, before <c>BehaviourTreeBindingSystem_Spawn</c> resolves it. Progenitor never
    /// compiles a tree; this only selects one the game already ships. A name that is unknown
    /// or whose prefab carries no <c>BehaviourTree</c> is refused with one log line and the
    /// unit keeps its own prefab's tree.
    /// </param>
    public static IUnitHandle Spawn(string prefab, UnitVec3 position, object? owner = null, string? pluginId = null, string? behaviourTree = null)
    {
        EnsureMainThread(nameof(Spawn));
        if (string.IsNullOrWhiteSpace(prefab))
            throw new ArgumentException("Prefab name or GUID hash is required.", nameof(prefab));

        string trimmed = prefab.Trim();
        int guid = 0;
        string name = trimmed;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            guid = parsed;
            name = trimmed;
        }

        return SpawnCore(name, guid, position, owner, pluginId, behaviourTree);
    }

    /// <summary>Instantiate by PrefabGUID hash. Same gating and ownership as the name overload.</summary>
    public static IUnitHandle Spawn(int prefabGuid, UnitVec3 position, object? owner = null, string? pluginId = null, string? behaviourTree = null)
    {
        EnsureMainThread(nameof(Spawn));
        return SpawnCore(prefabGuid.ToString(CultureInfo.InvariantCulture), prefabGuid, position, owner, pluginId, behaviourTree);
    }


    /// <summary>
    /// Spawn with <see cref="UnitSpawnOptions"/>: optional behavior tree, lifetime, instance
    /// stats / team / VBlood strip, and a <see cref="UnitSpawnOptions.Configure"/> callback.
    /// Callback timing: see <see cref="UnitSpawnOptions"/>. Validates options before spawn.
    /// </summary>
    public static IUnitHandle Spawn(string prefab, UnitVec3 position, UnitSpawnOptions options, object? owner = null, string? pluginId = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Validate();
        EnsureMainThread(nameof(Spawn));
        if (string.IsNullOrWhiteSpace(prefab))
            throw new ArgumentException("Prefab name or GUID hash is required.", nameof(prefab));

        string trimmed = prefab.Trim();
        int guid = 0;
        string name = trimmed;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            guid = parsed;
            name = trimmed;
        }

        return SpawnCore(name, guid, position, owner, pluginId, options.BehaviourTree, options);
    }

    /// <summary>Instantiate by PrefabGUID hash with <see cref="UnitSpawnOptions"/>.</summary>
    public static IUnitHandle Spawn(int prefabGuid, UnitVec3 position, UnitSpawnOptions options, object? owner = null, string? pluginId = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Validate();
        EnsureMainThread(nameof(Spawn));
        return SpawnCore(prefabGuid.ToString(CultureInfo.InvariantCulture), prefabGuid, position, owner, pluginId, options.BehaviourTree, options);
    }
    /// <summary>
    /// Point <paramref name="unit"/> at <paramref name="target"/> and request Combat.
    /// Does not clear <c>Immortal</c> and does not use Follow. False fields mean that
    /// half did not land; the call does not throw for a missing aggro component.
    /// </summary>
    public static UnitEngageResult Engage(UnitId unit, UnitId target)
    {
        EnsureMainThread(nameof(Engage));
#if PROGENITOR_TESTS
        return CurrentRuntime().Engage(unit, target);
#else
        if (CurrentRuntime() is UnityUnitRuntime runtime)
            return runtime.Engage(unit, target);
        return default;
#endif
    }

    /// <summary>
    /// Point <paramref name="unit"/> at <paramref name="target"/> and request Combat.
    /// Does not clear <c>Immortal</c> and does not use Follow. False fields mean that
    /// half did not land; the call does not throw for a missing aggro component.
    /// </summary>

    /// <summary>
    /// Dispose every handle recorded for <paramref name="owner"/> (despawn), and nothing else.
    /// PluginContext unload does this via Track; call it yourself if you used the static API
    /// with an owner, including to retire one subsystem while the rest of the plugin runs on.
    /// <para>
    /// This is the whole contract, and it is narrower than what plugin unload does (job FUNIT6,
    /// R59 finding 4). The assembly-wide teardown — end the sequence of every other live handle
    /// this plugin is responsible for, take its <see cref="UnitStep.Then"/> delegates out of
    /// everybody else's — is <see cref="SweepForUnload"/>, which only the reloader calls. FUNIT5
    /// had put that behavior behind this signature, so <c>Units.RemoveAll(subsystemA)</c>
    /// published <c>Stopped</c> with reason "plugin … unloaded" on subsystems B and C of a
    /// plugin that was not unloading at all.
    /// </para>
    /// </summary>
    public static void RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EnsureMainThread(nameof(RemoveAll));
        DespawnOwned(owner, null);
    }

    /// <summary>
    /// Plugin unload: despawn what <paramref name="owner"/> owns by reference, then run the
    /// assembly-wide sequence sweep below. <see cref="Reload.PluginReloader.SweepRegistrations"/>
    /// is the only caller, and it passes the plugin's BepInEx id, which is the same string
    /// <c>PluginContext.Guid</c> records on every handle it spawns.
    /// <para>
    /// The id is a parameter rather than something read off <paramref name="owner"/> (job
    /// FUNIT6, R59 finding 1). FUNIT5 read it from a public <c>GUID</c> / <c>Guid</c> property,
    /// and no BepInEx plugin type has one — <c>BasePlugin</c>'s public surface is
    /// <c>Log</c>, <c>Config</c>, <c>Load</c>, <c>Unload</c>, <c>AddComponent</c>, and the
    /// metadata id is reached only through <c>MetadataHelper</c> — so in the game the id half
    /// of the match was always null and only the test double it was written against had one.
    /// </para>
    /// </summary>
    internal static void SweepForUnload(object owner, string? pluginId)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EnsureMainThread(nameof(SweepForUnload));
        string? id = string.IsNullOrWhiteSpace(pluginId) ? null : pluginId!.Trim();
        DespawnOwned(owner, id);
        SweepSequences(owner, id);
    }

    private static void DespawnOwned(object owner, string? pluginId)
    {
        UnitHandle[] snapshot;
        lock (Gate)
        {
            var list = new List<UnitHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (ReferenceEquals(Live[i].Owner, owner))
                    list.Add(Live[i]);
            }

            snapshot = list.ToArray();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: RemoveAll dispose threw: " + e.Message); }
        }

        if (snapshot.Length > 0)
            Plugin.Logger?.LogInfo("Units: swept " + snapshot.Length + " unit(s) of " + NameFor(owner, pluginId, snapshot));
    }

    /// <summary>
    /// Who the line names. The id the reloader passed, else the id the swept handles were
    /// spawned with when they agree on one — that is the plugin's own <c>[BepInPlugin]</c>
    /// GUID, recorded at <c>Spawn</c> — else the owner's type name. Reading an id off the owner
    /// object was the third option and it never worked (job FUNIT6, R59 finding 1).
    /// </summary>
    private static string NameFor(object owner, string? pluginId, UnitHandle[] swept)
    {
        if (pluginId != null) return pluginId;

        string? shared = null;
        for (int i = 0; i < swept.Length; i++)
        {
            string id = swept[i].PluginId;
            if (string.IsNullOrEmpty(id)) return InferPluginId(owner);
            if (shared == null) shared = id;
            else if (!string.Equals(shared, id, StringComparison.Ordinal)) return InferPluginId(owner);
        }

        return shared ?? InferPluginId(owner);
    }

    /// <summary>
    /// The owner match in <see cref="DespawnOwned"/> cannot see every handle this plugin is responsible for, nor every
    /// sequence holding a delegate it wrote — and since <see cref="UnitStep.Then"/> such a
    /// delegate is a target and a method in the unloading collectible ALC, enough to refuse a
    /// Core reload (R55 finding 9). So after despawning what the sweep argument owns by
    /// reference, this walks <b>every</b> live handle.
    /// <para>
    /// <b>Whose handle is it (settled by R58 finding 3).</b> Ownership here is settled the same
    /// way <see cref="RemoveAll"/> settles it, only wider: a handle is the unloading plugin's
    /// when it was spawned with that plugin's <c>owner</c> object (the same reference, or any
    /// object whose type its assembly declares) <b>or</b> with the <paramref name="pluginId"/>
    /// the reloader passed. Both are recorded on the handle, and either alone is enough. The
    /// assembly test on its own was not: the public API takes <c>object owner</c> and invites a
    /// bare token, and <c>new object()</c> is declared in the BCL, so a plugin's own patrol was
    /// filed as a stranger's and left playing with no event — exactly the state R57 finding 1
    /// measured.
    /// </para>
    /// <para>
    /// <b>What each one gets:</b>
    /// <list type="bullet">
    /// <item>The unloading plugin's own sequence ends <see cref="UnitSequenceOutcome.Stopped"/>,
    /// published unless the handle is already disposed, with the move wait cleared and logged.
    /// This no longer depends on the sequence holding a callback: a patrol of GoTo / Wait / Face
    /// steps holds no delegate at all, and it is the shape <c>.rat patrol</c> itself uses, so
    /// gating the visit on a callback left the commonest case unswept (R58 finding 3).</item>
    /// <item>Anybody else's sequence — another plugin's, or an ownerless static-API spawn —
    /// keeps playing, with only the unloading assembly's <c>Then</c> steps neutralized, one log
    /// line per handle naming the plugin and the count. A neutralized step keeps its slot and
    /// reports <see cref="UnitStepOutcome.Skipped"/> with a reason (R58 finding 6).</item>
    /// </list>
    /// The unit stays in both cases: it is not this plugin's to despawn, and nothing but the
    /// delegate pinned anything. Dropping a third party's sequence outright was the pre-FUNIT4
    /// behavior and was wrong — <c>HoldsCallbackFrom</c> also matches a step naming a helper
    /// the unloading plugin <i>published</i>, which is the normal way one mod extends another.
    /// </para>
    /// </summary>
    private static void SweepSequences(object owner, string? pluginId)
    {
        System.Reflection.Assembly assembly;
        try { assembly = owner.GetType().Assembly; }
        catch { return; }

        UnitHandle[] handles;
        lock (Gate)
            handles = Live.ToArray();

        string name = assembly.GetName().Name ?? assembly.FullName ?? "an unloading plugin";
        int ownerless = 0;
        for (int i = 0; i < handles.Length; i++)
        {
            var handle = handles[i];
            bool mine;
            try { mine = OwnedByUnloadingPlugin(handle, owner, assembly, pluginId); }
            catch { continue; }

            if (mine)
            {
                // Only a sequence that was actually running is worth a line: the sweep now
                // visits every handle, and an idle one has nothing to report.
                if (!handle.StopSequenceForUnload("plugin " + name + " unloaded")) continue;
                Plugin.Logger?.LogInfo("Units: stopped the sequence on entity=" + handle.Entity
                    + " prefab='" + handle.Prefab + "' (owner: " + InferPluginId(handle.Owner)
                    + "); the plugin that owns it, " + name + ", is unloading. The unit itself stays.");
                continue;
            }

            int dropped;
            try { dropped = handle.DropCallbacksFrom(assembly); }
            catch { continue; }
            if (dropped == 0) continue;

            if (handle.Owner == null) ownerless++;
            Plugin.Logger?.LogInfo("Units: dropped " + dropped + " Then callback(s) written in "
                + name + " from the sequence on entity=" + handle.Entity
                + " prefab='" + handle.Prefab + "' (owner: "
                + (handle.Owner == null ? "none" : InferPluginId(handle.Owner))
                + "); those steps now do nothing and the rest of the sequence keeps running.");
        }

        if (ownerless > 0)
        {
            Plugin.Logger?.LogInfo("Units: " + ownerless
                + " of those handle(s) had no owner; pass owner: to Units.Spawn so the unit itself is swept too.");
        }
    }

    /// <summary>
    /// The ownership rule the sweep runs on: the same owner object, any owner object whose type
    /// the unloading assembly declares, or the unloading plugin's own id recorded at Spawn.
    /// <para>
    /// The id route is what makes <c>owner: new object()</c> work. Reference equality alone
    /// misses a second object registered as owner (a manager, a helper) and that object pins
    /// the same ALC; the assembly test alone misses a bare token, whose type is declared in the
    /// BCL. The id is only ever the BepInEx plugin id the reloader hands
    /// <see cref="SweepForUnload"/> — never the type-name fallback
    /// <see cref="InferPluginId"/> prints in a log line, so two plugins cannot collide on it.
    /// </para>
    /// </summary>
    private static bool OwnedByUnloadingPlugin(
        UnitHandle handle,
        object owner,
        System.Reflection.Assembly assembly,
        string? pluginId)
    {
        var handleOwner = handle.Owner;
        if (handleOwner != null)
        {
            if (ReferenceEquals(handleOwner, owner)) return true;
            try
            {
                if (ReferenceEquals(handleOwner.GetType().Assembly, assembly)) return true;
            }
            catch { /* an owner that cannot answer is not a match */ }
        }

        return pluginId != null && string.Equals(handle.PluginId, pluginId, StringComparison.Ordinal);
    }

    internal static void Install()
    {
        lock (Gate)
        {
            if (IsModuleSkipped())
            {
                _skipped = true;
                _installed = true;
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(ModuleName));
                return;
            }

            _skipped = false;
            _installed = true;
            _runtime ??= CreateDefaultRuntime();
        }

        HookFrame();
        Plugin.Logger?.LogInfo(_runtime is { IsServer: true }
            ? "Units: installed (server)"
            : "Units: installed (client no-op)");
#if !PROGENITOR_TESTS
        if (_runtime is UnityUnitRuntime)
        {
            try { Dev.MainThread.PostDelayed(0, ResumeHandedOffDespawns, Gate); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: despawn resume defer threw: " + e.Message); }
            // Lifetimes are stamped on the entity, so this generation enforces the ones an earlier
            // generation or an earlier process set (UnitExpiryPolicy).
            try { UnityUnitExpiry.Install(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: lifetime sweep install threw: " + e.Message); }
        }
#endif
    }

    internal static void Uninstall()
    {
#if !PROGENITOR_TESTS
        try { UnityUnitExpiry.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Units: lifetime sweep uninstall threw: " + e.Message); }
#endif
        UnhookFrame();
        UnitHandle[] snapshot;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            Live.Clear();
            _installed = false;
            _skipped = false;
        }

        int leftover = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            bool enabled = snapshot[i].Enabled;
            try { snapshot[i].Dispose(); if (enabled) leftover++; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: Uninstall dispose threw: " + e.Message); }
        }

#if !PROGENITOR_TESTS
        HandOffInFlightDespawns();
#endif

        lock (Gate)
            _runtime = null;

        Plugin.Logger?.LogInfo("Units: uninstalled leftover=" + leftover);
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Core uninstall drops the delayed poll that finishes each despawn, which left units in the
    /// world without their buffs (live, 2026-09-28). Hand those still waiting on a poll to
    /// Bootstrap, stamped with the server world, so the next generation finishes them
    /// (<see cref="ResumeHandedOffDespawns"/>).
    /// </summary>
    private static void HandOffInFlightDespawns()
    {
        try
        {
            if (_runtime is not UnityUnitRuntime runtime) return;
            var rows = runtime.InFlightDespawns();
            if (rows.Count == 0) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogWarning(DespawnHandoffPolicy.FormatHandOff(rows.Count, -1) + " (no server world at uninstall)");
                return;
            }
            int accepted = BootstrapLink.TryHandOffDespawns(DespawnHandoffPolicy.Pack(rows), world.SequenceNumber);
            string line = DespawnHandoffPolicy.FormatHandOff(rows.Count, accepted);
            if (accepted < 0) Plugin.Logger?.LogWarning(line);
            else Plugin.Logger?.LogInfo(line);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: despawn hand-off threw: " + e.Message);
        }
    }

    /// <summary>
    /// Take the despawns an earlier Core generation handed to Bootstrap in this server world and
    /// start each again through <see cref="UnityUnitRuntime.Despawn"/>: a fresh buff sweep, the
    /// replicated unit destroy, and the unit's live summons (their sweep was dropped too). Bootstrap drops rows from a replaced or
    /// reloaded world; an id that is gone, already being destroyed or now another prefab is left.
    /// </summary>
    private static void ResumeHandedOffDespawns()
    {
        try
        {
            if (CurrentRuntimeOrNull() is not UnityUnitRuntime runtime) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var rows = BootstrapLink.TryTakeDespawns(world.SequenceNumber, out int stale);
            int count = DespawnHandoffPolicy.RowCount(rows);
            if (rows == null || (count == 0 && stale == 0)) return;

            var em = world.EntityManager;
            int resumed = 0, gone = 0, otherPrefab = 0, refused = 0;
            for (int i = 0; i < count; i++)
            {
                int at = i * DespawnHandoffPolicy.Stride;
                var entity = new Entity { Index = rows[at], Version = rows[at + 1] };
                bool exists = em.Exists(entity);
                bool destroying = exists && entity.Has<DestroyTag>(em);
                bool samePrefab = exists && UnityUnitRuntime.ReadPrefabHash(em, entity) == rows[at + 2];
                switch (DespawnHandoffPolicy.Decide(exists, destroying, samePrefab))
                {
                    case DespawnResume.Gone: gone++; break;
                    case DespawnResume.OtherPrefab: otherPrefab++; break;
                    default:
                        if (runtime.Despawn(UnitId.From(entity))) resumed++;
                        else refused++;
                        break;
                }
            }

            Plugin.Logger?.LogInfo(DespawnHandoffPolicy.FormatResume(resumed, gone, otherPrefab, refused, stale));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: despawn resume threw: " + e.Message);
        }
    }

    private static IUnitRuntime? CurrentRuntimeOrNull()
    {
        lock (Gate) return _runtime;
    }
#endif

    internal static string DescribePins()
    {
        if (_skipped)
            return HarmonySlotHandoff.FormatModulePinsSkipped(ModuleName);

        var holds = new List<string>();
        lock (Gate)
        {
            if (Live.Count > 0)
                holds.Add("handles=" + Live.Count);
            if (_installed)
                holds.Add("installed");
        }

        return HarmonySlotHandoff.FormatModulePins(ModuleName, holds);
    }

    /// <summary>
    /// Enabled, undisposed handles spawned with exactly <paramref name="owner"/>: the units that
    /// plugin spawned and still owns. Feeds the one-time capacity warning in <c>ctx.Units</c>.
    /// </summary>
    internal static int LiveCountFor(object owner)
    {
        if (owner == null) return 0;
        int count = 0;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var handle = Live[i];
                if (ReferenceEquals(handle.Owner, owner) && handle.Enabled && !handle.IsDisposed) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Enabled, undisposed handles spawned with exactly <paramref name="owner"/>, copied under the
    /// gate. The Owned scope of <c>ctx.Units.ClearArea</c> starts from this list.
    /// </summary>
    internal static UnitHandle[] LiveOwnedBy(object owner)
    {
        if (owner == null) return Array.Empty<UnitHandle>();
        var list = new List<UnitHandle>();
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var handle = Live[i];
                if (ReferenceEquals(handle.Owner, owner) && handle.Enabled && !handle.IsDisposed)
                    list.Add(handle);
            }
        }
        return list.ToArray();
    }

    /// <summary>True when the Units module was skipped through SkipCoreModules.</summary>
    internal static bool IsSkipped => _skipped;

    /// <summary>The runtime every Units call goes through (created on first use).</summary>
    internal static IUnitRuntime Runtime => CurrentRuntime();

    /// <summary>Entity ids of this generation's enabled, undisposed handles, copied under the gate. Valid for one pass.</summary>
    internal static HashSet<UnitId> LiveUnitIds()
    {
        var ids = new HashSet<UnitId>();
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var handle = Live[i];
                if (handle.Enabled && !handle.IsDisposed && !handle.Entity.IsNone) ids.Add(handle.Entity);
            }
        }
        return ids;
    }

    /// <summary>
    /// The lifetime sweep's removal (<see cref="UnitExpirySweep"/>): a unit past its lifetime. A live
    /// handle of this generation is disposed, so its state, sequence and owner bookkeeping end the
    /// way <see cref="IUnitHandle.Despawn"/> ends them; a unit with no handle (spawned by an earlier
    /// generation or an earlier process) is despawned through <paramref name="runtime"/>.
    /// </summary>
    internal static bool ExpireUnit(UnitId unit, IUnitRuntime runtime)
    {
        if (unit.IsNone) return false;
        UnitHandle? owned = null;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                var handle = Live[i];
                if (handle.Entity == unit && handle.Enabled && !handle.IsDisposed)
                {
                    owned = handle;
                    break;
                }
            }
        }

        if (owned != null)
        {
            owned.Dispose();
            return true;
        }

        return runtime != null && runtime.Despawn(unit);
    }

    internal static void Forget(UnitHandle handle)
    {
        lock (Gate)
            Live.Remove(handle);
    }

    /// <summary>Main-thread poll of live Follow / GoTo handles. Hooked to <see cref="Plugin.Frame"/> live.</summary>
    internal static void Poll()
    {
        // Reused buffer: no ToArray per frame. Skip the copy when nobody is steering.
        lock (Gate)
        {
            bool any = false;
            for (int i = 0; i < Live.Count; i++)
            {
                if (Live[i].IsSteering)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                PollBuffer.Clear();
                return;
            }

            PollBuffer.Clear();
            for (int i = 0; i < Live.Count; i++)
                PollBuffer.Add(Live[i]);
        }

        for (int i = 0; i < PollBuffer.Count; i++)
        {
            try { PollBuffer[i].Poll(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: Poll threw: " + e.Message); }
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Live.Clear();
            PollBuffer.Clear();
            _runtime = null;
            _installed = false;
            _skipped = false;
            _frameHooked = false;
            TestClock = null;
        }
    }

    internal static int LiveCountForTests
    {
        get { lock (Gate) return Live.Count; }
    }

    internal static void SetRuntimeForTests(IUnitRuntime runtime)
    {
        lock (Gate)
            _runtime = runtime ?? NullUnitRuntime.Instance;
    }

    /// <summary>Null by default; a Wait is a value in tests, not a sleep.</summary>
    private static Func<double>? TestClock;

    internal static void SetClockForTests(Func<double>? clock)
    {
        lock (Gate)
            TestClock = clock;
    }

    internal static void SetSkippedForTests(bool skipped)
    {
        lock (Gate)
            _skipped = skipped;
    }

    internal static void TickForTests() => Poll();
#endif

    private static IUnitHandle SpawnCore(string prefab, int prefabGuid, UnitVec3 position, object? owner, string? pluginId, string? behaviourTree, UnitSpawnOptions? options = null)
    {
        string id = string.IsNullOrWhiteSpace(pluginId)
            ? InferPluginId(owner)
            : pluginId.Trim();

        if (_skipped)
        {
            Plugin.Logger?.LogInfo("Units: Spawn('" + prefab + "') skipped; returning a disabled handle.");
            return Track(new UnitHandle(prefab, id, owner, UnitId.None, position, runtime: null, enabled: false));
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsServer)
        {
            LogDebug("Units: Spawn('" + prefab + "') is server-only; returning a disabled handle.");
            return Track(new UnitHandle(prefab, id, owner, UnitId.None, position, runtime: null, enabled: false));
        }

        UnitId entity = UnitId.None;
        try { entity = runtime.Spawn(prefab, prefabGuid, position); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: Spawn('" + prefab + "') threw: " + e);
        }

        bool enabled = !entity.IsNone;
        if (!enabled)
        {
            string? miss = runtime.LastMissReason;
            Plugin.Logger?.LogInfo(string.IsNullOrEmpty(miss)
                ? "Units: no prefab named '" + prefab + "' on this server; returning a disabled handle."
                : miss);
        }
        else
        {
            Plugin.Logger?.LogInfo("Units: spawned '" + prefab + "' entity=" + entity + " at " + position);
            SelectTree(runtime, entity, behaviourTree);
#if !PROGENITOR_TESTS
            // Core's API-spawn marker: ally-safe hits apply only to casters that wear it.
            if (runtime is UnityUnitRuntime) Progenitor.Ai.ApiSpawnMarker.OnSpawned(entity.ToEntity());
#endif
        }

        var handle = Track(new UnitHandle(prefab, id, owner, entity, position, runtime, enabled));
        if (enabled && options != null)
            ApplySpawnOptions(handle, options);
        return handle;
    }

    /// <summary>
    /// Monotonic seconds for the step timers. Same source <c>GodMode</c> uses;
    /// <c>World.Time.ElapsedTime</c> reads 0 through the interop (CLAUDE.md trap 6).
    /// </summary>

    /// <summary>
    /// Lifetime / team / strip / stats from options, then <see cref="UnitSpawnOptions.Configure"/>.
    /// Configure runs only for an enabled handle; throws are logged.
    /// </summary>
    private static void ApplySpawnOptions(IUnitHandle handle, UnitSpawnOptions options)
    {
        if (options.LifetimeSeconds is float life)
        {
            if (!handle.TrySetLifetime(life))
                Plugin.Logger?.LogWarning("Units: spawn LifetimeSeconds=" + life + " refused on " + handle.Entity);
        }

        if (options.TeamValue != null || options.FactionIndex != null)
        {
            if (!handle.TrySetTeam(options.TeamValue, options.FactionIndex))
                Plugin.Logger?.LogWarning("Units: spawn team write refused on " + handle.Entity);
        }

        if (options.StripVBlood)
        {
            var strip = handle.TryStripVBlood();
            if (!strip.Ok)
                Plugin.Logger?.LogWarning("Units: spawn StripVBlood refused on " + handle.Entity);
            else
                LogDebug("Units: " + UnitInstanceMutation.StripSummary(strip.CanBeConsumedCleared, strip.RemovedComponents)
                    + " entity=" + handle.Entity);
        }

        if (options.Stats != null && !options.Stats.IsEmpty)
        {
            var result = handle.TrySetStats(options.Stats);
            if (result.Refusal != null)
                Plugin.Logger?.LogWarning("Units: spawn Stats refused on " + handle.Entity + ": " + result.Refusal);
            else if (result.PendingSpawnComputation)
                LogDebug("Units: spawn Stats pending spawn computation on " + handle.Entity);
        }

        if (options.Configure == null) return;
        try { options.Configure(handle); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: spawn Configure callback threw: " + e.Message);
        }
    }

    internal static double MonotonicSeconds()
    {
#if PROGENITOR_TESTS
        var clock = TestClock;
        if (clock != null) return clock();
#endif
        return Environment.TickCount64 / 1000.0;
    }

    /// <summary>
    /// Point the fresh unit at a vanilla behavior tree. Called on the spawn tick, right
    /// after <c>Instantiate</c>: <c>BehaviourTreeBindingSystem_Spawn</c> lives in
    /// <c>ReactToSpawnGroup</c> and resolves the binding once, so
    /// this is the only moment the selection is free. Never fails the spawn.
    /// </summary>
    private static void SelectTree(IUnitRuntime runtime, UnitId entity, string? behaviourTree)
    {
        if (string.IsNullOrWhiteSpace(behaviourTree)) return;
        string tree = behaviourTree!.Trim();

        if (runtime is not IUnitBehaviourRuntime behaviour)
        {
            Plugin.Logger?.LogInfo("Units: behaviourTree '" + tree + "' ignored; this runtime has no tree selection.");
            return;
        }

        int guid = 0;
        if (int.TryParse(tree, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            guid = parsed;

        bool ok;
        string? refusal;
        try { ok = behaviour.SelectBehaviourTree(entity, tree, guid, out refusal); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: behaviourTree '" + tree + "' threw: " + e.Message);
            return;
        }

        Plugin.Logger?.LogInfo(ok
            ? "Units: behavior tree '" + tree + "' bound on " + entity
            : string.IsNullOrEmpty(refusal)
                ? "Units: behavior tree '" + tree + "' refused on " + entity + "; keeping the prefab's own tree."
                : refusal!);
    }

    private static IUnitRuntime CurrentRuntime()
    {
        lock (Gate)
        {
            if (_runtime != null) return _runtime;
            _runtime = CreateDefaultRuntime();
            return _runtime;
        }
    }

    private static IUnitRuntime CreateDefaultRuntime()
    {
#if PROGENITOR_TESTS
        return NullUnitRuntime.Instance;
#else
        if (!Plugin.IsServerProcess)
            return NullUnitRuntime.Instance;
        return new UnityUnitRuntime();
#endif
    }

    private static bool IsModuleSkipped()
    {
        if (HarmonySlotHandoff.SkipModulesRefusedLiveWorld) return false;
        if (HarmonySlotHandoff.ShouldSkipModule(ModuleName)) return true;
#if !PROGENITOR_TESTS
        string csv = BootstrapLink.SkipCoreModules;
        if (string.IsNullOrWhiteSpace(csv)) return false;
        var parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i].Trim(), ModuleName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
#endif
        return false;
    }

    private static UnitHandle Track(UnitHandle handle)
    {
        lock (Gate)
            Live.Add(handle);
        return handle;
    }

    /// <summary>
    /// A name for an owner object in a log line. Never an identity: two plugins can share a
    /// type name, so nothing matches on this — the sweep's id match
    /// (<see cref="OwnedByUnloadingPlugin"/>) only ever uses the BepInEx id the reloader passes
    /// to <see cref="SweepForUnload"/>. Reading a <c>GUID</c> property off the owner was tried
    /// (FUNIT5) and removed by job FUNIT6: no BepInEx plugin type declares one.
    /// </summary>
    private static string InferPluginId(object? owner) =>
        owner == null ? "unowned" : owner.GetType().FullName ?? "unowned";

    private static void HookFrame()
    {
#if !PROGENITOR_TESTS
        if (_frameHooked) return;
        Plugin.Frame += Poll;
        _frameHooked = true;
#else
        _frameHooked = true;
#endif
    }

    private static void UnhookFrame()
    {
        if (!_frameHooked) return;
#if !PROGENITOR_TESTS
        Plugin.Frame -= Poll;
#endif
        _frameHooked = false;
    }

    /// <summary>
    /// Effects does not marshal off-thread; Units throws because EntityManager is not
    /// thread-safe. Tests skip the check (no captured main-thread id).
    /// </summary>
    internal static void EnsureMainThread(string op)
    {
#if PROGENITOR_TESTS
        _ = op;
#else
        if (Dev.MainThread.IsMainThread) return;
        throw new InvalidOperationException(
            "Units." + op + " must run on the game main thread (Plugin.Frame, a command, or EventBus). Effects does not marshal off-thread either; Units throws because EntityManager writes crash natively from a worker.");
#endif
    }

    internal static void LogDebug(string line)
    {
#if PROGENITOR_TESTS
        _ = line;
#else
        Plugin.Logger?.LogDebug(line);
#endif
    }
}
