#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using ProjectM;
using Progenitor.Config;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.WorldState;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;

namespace Progenitor.Ai;

/// <summary>
/// Core default, server only, for units spawned through the API: every <b>damaging</b> hit slot
/// whose hit condition has no team source gets a caster-scoped guard in front of it —
/// "if the caster (the condition's Owner, or its MinionMaster) wears the API-spawn marker
/// (<see cref="ApiSpawnMarker"/>), the target must not be an ally" — so hits (damage, hit buffs,
/// knockback) cast by API-spawned units skip allies, and every other caster's hit runs the original
/// condition unchanged. Whether Owner is the caster and MinionMaster reaches a summon's master is
/// not verified live. A <c>CreateGameplayEventsOnHit</c> slot is damaging when a <c>GameplayEventListeners</c>
/// row on the same prefab has type <c>DealDamage</c> and the slot's <c>EventId</c> (both fields).
/// Chains through Branch, Delay or Iterate events are not followed; other slots are never touched.
/// Each such slot is pointed at a private guarded copy of its condition blob
/// (<see cref="AllySafetyCodec"/>); the shared original is never written and nothing is ever
/// freed. Live ability entities already spawned keep the condition they were spawned with.
///
/// <para>Runs on the first frame after every world load (<see cref="WorldEpoch.Advanced"/>, the
/// <c>CopyDataFromPrefabs</c> postfix: every prefab exists by then), 30 s after the server
/// <see cref="WorldReady"/> (a brand-new world loads no save), when Core installs into a running
/// world, 1 s after a content-registry prefab is registered, and when <c>[Ai] AllySafeHits</c>
/// changes. Every pass is idempotent: a guarded slot is recognized by its copy's trailer, never
/// guarded twice. Uninstall (Core swap or unload) points every guarded slot back at its original.</para>
///
/// <para>Evidence: the 2026-09-24 boss AI tree review (internal research notes). The patch
/// mechanism (unconditional form) was verified live by hand; this caster-scoped implementation is
/// unit-tested, not yet verified live.</para>
/// </summary>
internal static unsafe class AllySafety
{
    internal const string ConfigSection = "Ai";
    internal const string ConfigKey = "AllySafeHits";
    internal const string ConfigDescription =
        "Make damaging hits with no team check skip allies when the caster was spawned through the Progenitor API (ctx.Units.Spawn); "
        + "vanilla units and bosses are unchanged. Environmental hazards, player abilities and hits that also heal are left out. Server only. "
        + "Plugins can still exempt or protect single prefabs through ctx.Ai.AllySafety. Default true.";

    /// <summary>A prefab lists at most this many hit slots before the rest are ignored (vanilla maximum is far lower).</summary>
    internal const int MaxSlots = 64;

    /// <summary>Listener rows read per prefab (vanilla prefabs have a few dozen at most).</summary>
    internal const int MaxListeners = 512;

    private const int EmptyRetries = 6;

    private static bool _installed;
    private static int _generation;
    private static ConfigEntry<bool>? _enabled;
    private static ConfigFile? _configFile;
    private static IDisposable? _worldReadySub;
    private static bool _registeredQueued;
    private static readonly object HoldGate = new();
    private static readonly Dictionary<int, (int Exempt, int Protect)> Holds = new();

    /// <summary>The config switch; true when unbound (tests, no BepInEx config).</summary>
    internal static bool Enabled
    {
        get
        {
            try { return _enabled?.Value ?? true; }
            catch { return true; }
        }
    }

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        _generation++;
        // Binds now through Bootstrap's handover; with an older Bootstrap SharedConfig logs and
        // binds once the chainloader is complete (and the passes below retry as well).
        Config.SharedConfig.BindWhenAvailable("[" + ConfigSection + "] " + ConfigKey, file => BindConfig(file));
        WorldEpoch.Advanced += OnAdvanced;
        PrefabRegistry.Registered += OnRegistered;
        ConfigHelper.Reloaded += OnConfigReloaded;
        _worldReadySub = EventBus.On<WorldReady>(OnWorldReady);
        var world = Worlds.Server;
        if (world != null && world.IsCreated) Schedule("core install", 0.0, EmptyRetries);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        WorldEpoch.Advanced -= OnAdvanced;
        PrefabRegistry.Registered -= OnRegistered;
        ConfigHelper.Reloaded -= OnConfigReloaded;
        try { _worldReadySub?.Dispose(); }
        catch { }
        _worldReadySub = null;
        _installed = false;
        _generation++;
        _enabled = null;
        _configFile = null;
        _registeredQueued = false;
        lock (HoldGate) Holds.Clear();
        RestoreAll();
    }

    // ---- scheduling -----------------------------------------------------------------------------

    private static void OnAdvanced(int epoch) => Schedule("world load", 0.0, EmptyRetries);

    private static void OnWorldReady(WorldReady ev)
    {
        if (ev == null || !ev.IsServer) return;
        Schedule("world ready +30 s", 30.0, EmptyRetries);
    }

    private static void OnRegistered(PrefabDefinition def)
    {
        if (_registeredQueued) return;
        _registeredQueued = true;
        int generation = _generation;
        Dev.MainThread.PostDelayed(1.0, () =>
        {
            if (generation != _generation || !_installed) return;
            _registeredQueued = false;
            Run("prefab registered");
        });
    }

    private static void OnConfigReloaded(ConfigFile file)
    {
        if (file == null || !ConfigHelper.IsSharedConfigFile(file)) return;
        if (_enabled == null) BindConfig(file);
        Schedule("config reload", 0.0, 0);
    }

    private static void OnSettingChanged(object? sender, SettingChangedEventArgs args)
    {
        var def = args?.ChangedSetting?.Definition;
        if (def == null || !string.Equals(def.Section, ConfigSection, StringComparison.Ordinal)
            || !string.Equals(def.Key, ConfigKey, StringComparison.Ordinal)) return;
        Schedule("config change", 0.0, 0);
    }

    private static void Schedule(string why, double delay, int retries)
    {
        int generation = _generation;
        Dev.MainThread.PostDelayed(delay, () =>
        {
            if (generation != _generation || !_installed) return;
            // With a Bootstrap that predates the shared-config handover, the install-time bind
            // is deferred (Config.SharedConfig); a pass is another chance to take it.
            if (_enabled == null) BindConfig();
            var report = Run(why);
            // A pass that found no damaging prefab ran before the prefabs were there; try again.
            if (report != null && report.DamagingPrefabs == 0 && retries > 0)
                Schedule(why + " (retry)", 10.0, retries - 1);
        });
    }

    private static void BindConfig(ConfigFile? shared = null)
    {
        try
        {
            if (_enabled != null) return;
            if (shared == null)
            {
                shared = Config.SharedConfig.TryGet();
                if (shared == null) return; // SharedConfig logged the deferral; the next pass retries
            }
            _configFile = shared;
            _enabled = ConfigHelper.Bind(_configFile, ConfigSection, ConfigKey, true, ConfigDescription);
            ConfigHelper.AddSettingChanged(_configFile, OnSettingChanged);
            Plugin.Logger?.LogInfo("Ai: bound [" + ConfigSection + "] " + ConfigKey + " = " + _enabled.Value + ".");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: binding [" + ConfigSection + "] " + ConfigKey + " threw (" + e.Message + "); ally-safe hits stay on.");
        }
    }

    // ---- passes ---------------------------------------------------------------------------------

    /// <summary>One reconciling pass over every damaging hit prefab, with its summary line.</summary>
    internal static AllySafetyReport? Run(string why)
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return null;
            var report = Pass(world, write: true, restoreAll: false);
            Plugin.Logger?.LogInfo("Ai: ally-safe hits (" + why + ") " + report.Summary());
            foreach (var entry in report.Entries)
                if (entry.State == AllySafetyState.Failed)
                    Plugin.Logger?.LogWarning("Ai: ally-safe hits: " + entry);
            return report;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: ally-safe hits (" + why + ") threw: " + e.Message);
            return null;
        }
    }

    /// <summary>Read-only pass: what every damaging hit prefab looks like now.</summary>
    internal static AllySafetyReport Describe(World world) => Pass(world, write: false, restoreAll: false);

    /// <summary>Point every guarded slot back at its original (Core uninstall). Main thread only.</summary>
    private static void RestoreAll()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            if (!Dev.MainThread.IsMainThread)
            {
                // The copies stay recognizable by their trailer; the next generation adopts them.
                Plugin.Logger?.LogWarning("Ai: ally-safe hits: uninstall off the main thread; guarded slots left for the next Core generation.");
                return;
            }
            var report = Pass(world, write: true, restoreAll: true);
            Plugin.Logger?.LogInfo("Ai: ally-safe hits (core uninstall): restored " + report.RestoredSlots + " slot(s); " + report.Failures + " failure(s)");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: ally-safe hits restore threw: " + e.Message);
        }
    }

    private static AllySafetyReport Pass(World world, bool write, bool restoreAll)
    {
        var em = world.EntityManager;
        var report = new AllySafetyReport { Enabled = Enabled };
        var prefabs = Query.EntitiesWithOptions(
            em,
            EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            ComponentIndex.Of<CreateGameplayEventsOnHit>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<DealDamageOnGameplayEvent>(ComponentType.AccessMode.ReadOnly));
        foreach (var entity in prefabs)
        {
            if (entity.Index <= 0) continue;
            try
            {
                if (!entity.Has<PrefabTag>(em)) continue;
                int guid = AiRuntime.PrefabGuidOf(em, entity);
                if (guid == 0 || AiRuntime.PrefabEntity(world, guid) != entity) continue; // live instances and unmapped copies
                Reconcile(world, entity, guid, write, restoreAll, report);
            }
            catch (Exception e)
            {
                report.Failures++;
                Plugin.Logger?.LogWarning("Ai: ally-safe hits: prefab " + AiRuntime.Fmt(entity) + " threw: " + e.Message);
            }
        }
        return report;
    }

    /// <summary>Reconcile one prefab now (after a plugin hold changed). Main thread.</summary>
    internal static AllySafetyEntry? ReconcileOne(World world, int guid, bool write)
    {
        var entity = AiRuntime.PrefabEntity(world, guid);
        if (entity == Entity.Null || !IsDamagingHitPrefab(world.EntityManager, entity)) return null;
        var report = new AllySafetyReport { Enabled = Enabled };
        var entry = Reconcile(world, entity, guid, write, restoreAll: false, report);
        if (write && (report.NewlyGuardedSlots > 0 || report.RestoredSlots > 0 || report.Failures > 0))
            Plugin.Logger?.LogInfo("Ai: ally-safe hits: " + (entry?.ToString() ?? AiRuntime.Name(world, guid))
                + "; guarded " + report.NewlyGuardedSlots + ", restored " + report.RestoredSlots + ", " + report.Failures + " failure(s)");
        return entry ?? new AllySafetyEntry { Name = AiRuntime.Name(world, guid), Guid = guid, State = AllySafetyState.Protected, Reason = "every damaging hit slot tests the team, or no hit slot triggers DealDamage" };
    }

    internal static bool IsDamagingHitPrefab(EntityManager em, Entity entity) =>
        entity.Has<CreateGameplayEventsOnHit>(em) && entity.Has<DealDamageOnGameplayEvent>(em);

    private readonly struct Slot
    {
        internal Slot(int index, HitSlotKind kind, IntPtr pointer, long original, bool damaging, bool current)
        {
            Index = index;
            Kind = kind;
            Pointer = pointer;
            Original = original;
            Damaging = damaging;
            Current = current;
        }

        /// <summary>A guarded copy in the current (scoped, this marker) format; false for a first-format copy.</summary>
        internal bool Current { get; }

        internal int Index { get; }
        internal HitSlotKind Kind { get; }
        internal IntPtr Pointer { get; }
        internal long Original { get; }

        /// <summary>A DealDamage listener on the prefab listens for this slot's event.</summary>
        internal bool Damaging { get; }
    }

    /// <summary>
    /// The events a <c>DealDamage</c> listener on <paramref name="entity"/> reacts to, read from its
    /// <c>GameplayEventListeners</c> rows (stride <see cref="AllySafetyCodec.ListenerStride"/>).
    /// </summary>
    private static HashSet<(int Type, int Id)> DamageEvents(EntityManager em, Entity entity)
    {
        int stride = AllySafetyCodec.ListenerStride;
        byte* raw = AiRuntime.Buffer<GameplayEventListeners>(em, entity, stride, write: false, out int length);
        if (raw == null || length <= 0) return new HashSet<(int, int)>();
        int rows = Math.Min(length, MaxListeners);
        return AllySafetyCodec.DamageEvents(new ReadOnlySpan<byte>(raw, rows * stride), rows);
    }

    /// <returns>The entry for a prefab that is not simply team-aware, else null.</returns>
    private static AllySafetyEntry? Reconcile(World world, Entity entity, int guid, bool write, bool restoreAll, AllySafetyReport report)
    {
        var em = world.EntityManager;
        int stride = AllySafetyCodec.HitRowStride;
        byte* raw = AiRuntime.Buffer<CreateGameplayEventsOnHit>(em, entity, stride, write: false, out int length);
        report.DamagingPrefabs++;
        var damage = DamageEvents(em, entity);
        var slots = new List<Slot>();
        int blind = 0, nulls = 0, guarded = 0, unreadable = 0, damaging = 0, staleGuards = 0;
        for (int k = 0; raw != null && k < length && k < MaxSlots; k++)
        {
            byte* row = raw + k * stride;
            bool isDamaging = AllySafetyCodec.IsDamagingSlot(new ReadOnlySpan<byte>(row, stride), damage);
            byte* p = AiRuntime.BlobData(row + Layout.CreateGameplayEventsOnHitConditionOffset);
            var kind = ClassifyPointer(p, out long original, out bool current);
            if (!isDamaging)
            {
                // Only damaging slots are guarded. A guard on any other slot (an older build's) goes back.
                report.NonDamagingSlots++;
                if (kind == HitSlotKind.Guarded)
                {
                    staleGuards++;
                    slots.Add(new Slot(k, kind, (IntPtr)p, original, damaging: false, current));
                }
                continue;
            }
            damaging++;
            report.DamagingSlots++;
            switch (kind)
            {
                case HitSlotKind.TeamAware: report.TeamAwareSlots++; break;
                case HitSlotKind.TeamBlind: blind++; break;
                case HitSlotKind.NullCondition: nulls++; break;
                case HitSlotKind.Guarded: guarded++; break;
                case HitSlotKind.Unreadable: unreadable++; break;
            }
            slots.Add(new Slot(k, kind, (IntPtr)p, original, damaging: true, current));
        }
        if (damaging == 0 && staleGuards == 0)
        {
            report.PrefabsWithoutDamagingSlot++;
            return null;
        }
        if (blind == 0 && nulls == 0 && guarded == 0 && unreadable == 0 && staleGuards == 0)
        {
            report.TeamAwarePrefabs++;
            return null;
        }

        string name = AiRuntime.Name(world, guid);
        string? builtIn = AllySafetyRules.BuiltInExemption(name, entity.Has<HealOnGameplayEvent>(em));
        (int exempt, int protect) holds;
        lock (HoldGate) holds = Holds.TryGetValue(guid, out var h) ? h : (0, 0);
        bool want = !restoreAll && AllySafetyRules.ShouldGuard(report.Enabled, builtIn, holds.exempt, holds.protect);

        var entry = new AllySafetyEntry { Name = name, Guid = guid };
        int failures = unreadable;
        byte* rw = null;
        foreach (var slot in slots)
        {
            if (!write)
            {
                if (slot.Damaging) Count(entry, slot.Kind, slot.Original);
                continue;
            }
            try
            {
                if (!slot.Damaging)
                {
                    if (slot.Original != 0 && !IsLiveBlob((byte*)slot.Original))
                        throw new InvalidOperationException("slot " + slot.Index + ": the original condition is no longer a valid blob; left guarded");
                    if (rw == null) rw = Writable(em, entity, stride, slot.Index);
                    var field = (IntPtr*)(rw + slot.Index * stride + Layout.CreateGameplayEventsOnHitConditionOffset);
                    if (*field == slot.Pointer)
                    {
                        *field = (IntPtr)slot.Original;
                        report.RestoredSlots++;
                    }
                    staleGuards--;
                }
                else if (want && slot.Kind == HitSlotKind.Guarded && !slot.Current)
                {
                    // A first-format (unconditional) copy: rebuild the scoped guard from its original.
                    if (slot.Original != 0 && !IsLiveBlob((byte*)slot.Original))
                        throw new InvalidOperationException("slot " + slot.Index + ": the original condition is no longer a valid blob; left as it is");
                    byte* copy = MakeGuardedCopy((byte*)slot.Original);
                    if (rw == null) rw = Writable(em, entity, stride, slot.Index);
                    var field = (IntPtr*)(rw + slot.Index * stride + Layout.CreateGameplayEventsOnHitConditionOffset);
                    if (*field == slot.Pointer) *field = (IntPtr)copy;
                    report.NewlyGuardedSlots++;
                    Count(entry, HitSlotKind.Guarded, slot.Original == 0 ? 0 : 1);
                }
                else if (want && (slot.Kind == HitSlotKind.TeamBlind || slot.Kind == HitSlotKind.NullCondition))
                {
                    byte* copy = MakeGuardedCopy((byte*)slot.Pointer);
                    if (rw == null) rw = Writable(em, entity, stride, slot.Index);
                    *(IntPtr*)(rw + slot.Index * stride + Layout.CreateGameplayEventsOnHitConditionOffset) = (IntPtr)copy;
                    report.NewlyGuardedSlots++;
                    Count(entry, HitSlotKind.Guarded, slot.Kind == HitSlotKind.NullCondition ? 0 : 1);
                }
                else if (!want && slot.Kind == HitSlotKind.Guarded)
                {
                    if (slot.Original != 0 && !IsLiveBlob((byte*)slot.Original))
                        throw new InvalidOperationException("slot " + slot.Index + ": the original condition is no longer a valid blob; left guarded");
                    if (rw == null) rw = Writable(em, entity, stride, slot.Index);
                    var field = (IntPtr*)(rw + slot.Index * stride + Layout.CreateGameplayEventsOnHitConditionOffset);
                    if (*field == slot.Pointer)
                    {
                        *field = (IntPtr)slot.Original; // the copy stays allocated
                        report.RestoredSlots++;
                    }
                    Count(entry, slot.Original == 0 ? HitSlotKind.NullCondition : HitSlotKind.TeamBlind, 0);
                }
                else Count(entry, slot.Kind, slot.Original);
            }
            catch (Exception e)
            {
                failures++;
                if (slot.Damaging) Count(entry, slot.Kind, slot.Original);
                entry.Reason = e.Message;
            }
        }

        report.GuardedSlots += entry.GuardedSlots;
        report.GuardedNullSlots += entry.GuardedNullSlots;
        report.Failures += failures;
        if (failures > 0)
        {
            entry.State = AllySafetyState.Failed;
            if (entry.Reason.Length == 0) entry.Reason = unreadable + " hit condition(s) do not parse";
        }
        else if (staleGuards > 0 && !restoreAll)
        {
            // Read-only pass: a slot that deals no damage still points at a guarded copy.
            entry.State = AllySafetyState.Pending;
            entry.Reason = staleGuards + " non-damaging slot(s) still guarded; Core restores them on its next pass";
        }
        else if (entry.TeamBlindSlots == 0 && entry.NullConditionSlots == 0 && entry.GuardedSlots == 0)
        {
            // Only non-damaging guards were here, and they were just restored.
            report.TeamAwarePrefabs++;
            return null;
        }
        else if (entry.TeamBlindSlots == 0 && entry.NullConditionSlots == 0)
        {
            entry.State = AllySafetyState.Protected;
            entry.Reason = holds.protect > 0 && (builtIn != null || !report.Enabled) ? "protected by a plugin" : "guarded by default";
            report.ProtectedPrefabs++;
        }
        else if (restoreAll)
        {
            entry.State = AllySafetyState.Disabled;
            entry.Reason = "core uninstall";
        }
        else if (want)
        {
            // Only a read-only pass gets here: a writing pass guards these slots or fails.
            entry.State = AllySafetyState.Pending;
            entry.Reason = "Core guards it on its next pass";
        }
        else
        {
            entry.State = AllySafetyRules.UnguardedState(builtIn, holds.exempt);
            switch (entry.State)
            {
                case AllySafetyState.ExemptBuiltIn:
                    entry.Reason = builtIn!;
                    report.ExemptBuiltIn++;
                    break;
                case AllySafetyState.ExemptByPlugin:
                    entry.Reason = "exempted by a plugin";
                    report.ExemptByPlugin++;
                    break;
                default:
                    entry.Reason = "[" + ConfigSection + "] " + ConfigKey + " is false";
                    report.ExemptByConfig++;
                    break;
            }
        }
        report.Entries.Add(entry);
        return entry;
    }

    private static void Count(AllySafetyEntry entry, HitSlotKind kind, long original)
    {
        switch (kind)
        {
            case HitSlotKind.TeamBlind: entry.TeamBlindSlots++; break;
            case HitSlotKind.NullCondition: entry.NullConditionSlots++; break;
            case HitSlotKind.Guarded:
                entry.GuardedSlots++;
                if (original == 0) entry.GuardedNullSlots++;
                break;
        }
    }

    private static byte* Writable(EntityManager em, Entity entity, int stride, int index)
    {
        byte* rw = AiRuntime.Buffer<CreateGameplayEventsOnHit>(em, entity, stride, write: true, out int length);
        if (rw == null || index >= length) throw new InvalidOperationException("hit slot " + index + " is gone");
        return rw;
    }

    /// <summary>Classify the blob a slot points at. <paramref name="original"/> is a guarded copy's original.</summary>
    private static HitSlotKind ClassifyPointer(byte* p, out long original, out bool current)
    {
        original = 0;
        current = false;
        if (p == null) return HitSlotKind.NullCondition;
        if (!IsLiveBlob(p)) return HitSlotKind.Unreadable;
        var span = new ReadOnlySpan<byte>(p, AiRuntime.BlobLength(p));
        if (AllySafetyCodec.TryGuard(span, out original, out int format, out int marker))
        {
            current = format == 2 && marker == ApiSpawnMarker.Guid;
            return HitSlotKind.Guarded;
        }
        return AllySafetyCodec.Classify(false, span);
    }

    /// <summary>The header in front of <paramref name="p"/> names it (ValidationPtr) and has a sane length.</summary>
    private static bool IsLiveBlob(byte* p)
    {
        if (p == null) return false;
        byte* header = p - Layout.BlobAssetHeaderBytes;
        if (*(long*)(header + Layout.BlobAssetHeaderValidationPtrOffset) != (long)p) return false;
        return AiRuntime.BlobLength(p) > 0;
    }

    /// <summary>The guarded copy of <paramref name="original"/> (null = a slot with no condition). Deduplicated per original.</summary>
    private static byte* MakeGuardedCopy(byte* original)
    {
        if (original == null)
        {
            byte[] fresh = AllySafetyCodec.BuildNullCondition(ApiSpawnMarker.Guid);
            return AiRuntime.PrivateBlob(fresh, AllySafetyCodec.NewHeader(AllySafetyCodec.ContentHash(fresh)));
        }
        int length = AiRuntime.BlobLength(original);
        byte[] data = AllySafetyCodec.Prepend(new ReadOnlySpan<byte>(original, length), (long)original, ApiSpawnMarker.Guid);
        byte[] header = AllySafetyCodec.CopyHeader(new ReadOnlySpan<byte>(original - Layout.BlobAssetHeaderBytes, Layout.BlobAssetHeaderBytes));
        return AiRuntime.PrivateBlob(data, header);
    }

    // ---- plugin holds ---------------------------------------------------------------------------

    internal static void AddHold(int guid, bool exempt)
    {
        lock (HoldGate)
        {
            var h = Holds.TryGetValue(guid, out var v) ? v : (0, 0);
            Holds[guid] = exempt ? (h.Item1 + 1, h.Item2) : (h.Item1, h.Item2 + 1);
        }
    }

    internal static void ReleaseHold(int guid, bool exempt, int generation)
    {
        if (generation != _generation) return; // a Core swap already cleared the holds and restored
        lock (HoldGate)
        {
            if (!Holds.TryGetValue(guid, out var h)) return;
            h = exempt ? (Math.Max(0, h.Item1 - 1), h.Item2) : (h.Item1, Math.Max(0, h.Item2 - 1));
            if (h.Item1 == 0 && h.Item2 == 0) Holds.Remove(guid);
            else Holds[guid] = h;
        }
        void Apply()
        {
            if (generation != _generation) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            try { ReconcileOne(world, guid, write: true); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ai: ally-safe hits: re-applying " + guid + " threw: " + e.Message); }
        }
        if (Dev.MainThread.IsMainThread) Apply();
        else Dev.MainThread.Post(Apply);
    }

    internal static int Generation => _generation;
}

/// <summary>
/// <see cref="PluginAi.AllySafety"/>: Core's ally-safe hit default (docs/ai.md, "Ally-safe damaging
/// hits"). Core guards every damaging hit without a team check on its own; this API only reads it
/// and moves single prefabs in or out. Every hold is tracked by the plugin context.
/// </summary>
public sealed class AiAllySafety
{
    private readonly PluginAi _ai;

    internal AiAllySafety(PluginAi ai) => _ai = ai;

    /// <summary><c>[Ai] AllySafeHits</c> (default true).</summary>
    public bool Enabled => AllySafety.Enabled;

    /// <summary>
    /// Leave <paramref name="prefab"/> vanilla while the handle is held: its guarded slots are pointed
    /// back at their original conditions now, and guarded again on dispose if the default (or another
    /// plugin's <see cref="Protect"/>) wants them. Exempt wins over Protect. Throws when the prefab is
    /// not a damaging hit prefab.
    /// </summary>
    public IDisposable Exempt(string prefab) => Hold(prefab, exempt: true, nameof(Exempt));

    /// <summary>
    /// Guard <paramref name="prefab"/>'s team-blind hit slots while the handle is held, even when a
    /// built-in rule exempts it (a player ability, a hazard) or <c>[Ai] AllySafeHits</c> is false.
    /// A prefab whose hits all test the team already needs nothing. Dispose returns it to the default.
    /// </summary>
    public IDisposable Protect(string prefab) => Hold(prefab, exempt: false, nameof(Protect));

    /// <summary>
    /// True when <paramref name="prefab"/> is a damaging hit prefab none of whose damaging hit slots can hit an
    /// ally for lack of a team check: every slot tests the team or points at a guarded copy.
    /// </summary>
    public bool IsProtected(string prefab)
    {
        var world = _ai.ServerWorld(nameof(IsProtected));
        var entity = AiRuntime.RequirePrefab(world, prefab, "prefab", out int guid, out string name);
        if (!AllySafety.IsDamagingHitPrefab(world.EntityManager, entity))
            throw new InvalidOperationException("'" + name + "' is not a damaging hit prefab (CreateGameplayEventsOnHit + DealDamageOnGameplayEvent).");
        var entry = AllySafety.ReconcileOne(world, guid, write: false);
        return entry != null && entry.TeamBlindSlots == 0 && entry.NullConditionSlots == 0 && entry.State != AllySafetyState.Failed;
    }

    /// <summary>
    /// A read-only pass now: counts of damaging, team-aware, guarded and exempt prefabs, and one entry
    /// for every damaging prefab that is not simply team-aware. <c>ToString()</c> formats it.
    /// </summary>
    public AllySafetyReport Describe() => AllySafety.Describe(_ai.ServerWorld(nameof(Describe)));

    private IDisposable Hold(string prefab, bool exempt, string op)
    {
        var world = _ai.ServerWorld(op);
        var entity = AiRuntime.RequirePrefab(world, prefab, "prefab", out int guid, out string name);
        if (!AllySafety.IsDamagingHitPrefab(world.EntityManager, entity))
            throw new InvalidOperationException("'" + name + "' is not a damaging hit prefab (CreateGameplayEventsOnHit + DealDamageOnGameplayEvent).");
        int generation = AllySafety.Generation;
        AllySafety.AddHold(guid, exempt);
        try { AllySafety.ReconcileOne(world, guid, write: true); }
        catch
        {
            AllySafety.ReleaseHold(guid, exempt, generation);
            throw;
        }
        PluginAi.Log("ally-safe hits: " + (exempt ? "exempt " : "protect ") + name + " (" + guid.ToString(CultureInfo.InvariantCulture) + ")");
        return _ai.Track("ally-safe " + (exempt ? "exempt " : "protect ") + name, () => AllySafety.ReleaseHold(guid, exempt, generation));
    }
}
#endif
