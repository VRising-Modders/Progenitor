using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Plugins;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// Removes Progenitor-registered GUIDs whose mod is gone from players' unlock buffers, on the
/// server. Planning, the ledger format and the log lines are in <see cref="UnlockPrunePlan"/>.
///
/// <para><b>When.</b> (1) After a recipe unregisters, when the unload reason is
/// <see cref="PluginUnloadReason.Removal"/> or the handle was disposed outside an unload: every
/// user's buffers, offline players included, on the next frame (a re-register in the same frame
/// keeps the row). (2) <see cref="WorldLoadSweepDelaySeconds"/> after the save loads: every user
/// against every GUID in the persisted ledger (<c>BepInEx/config/progenitor-content-guids.json</c>),
/// which covers a world saved while a mod was present and loaded after it was removed. (3) When a
/// player connects: that player, the same way. The ledger sweeps skip a GUID whose mod this process
/// knows in any state but unloaded (<see cref="UnlockPrunePlan.ModStillPresent"/>).</para>
///
/// <para><b>The client's copy.</b> The server sends a player's progression list at join
/// (<c>SendInitialUnlockedProgressionEvent</c>, before <c>PlayerConnected</c> is published) and
/// nothing here sends a removal, so a connected client keeps a pruned GUID until its next join,
/// as the Dev op's <c>lock</c> showed live on 2026-10-06. The world-load sweep runs before most
/// joins, so the first join after a restart normally gets the clean list.</para>
///
/// <para><b>Write.</b> <c>UnlockedProgressionElement</c> (stride 4), <c>UnlockedRecipeElement</c> and
/// <c>UnlockedBlueprintElement</c> (stride 8) on the progression entity
/// <c>ProgressionMapper</c> names. Kept rows are copied byte for byte and written in one
/// <c>UnsafeEntityManagerUtility.SetBufferDataRaw</c> per changed buffer (the call the game's save
/// loader uses; the progression write is the one the Dev <c>unlock</c> op's <c>lock</c> ran live on
/// 2026-10-06), then read back. The derived rows are dropped directly rather than through the
/// game's rebuild event: the rebuild would not re-add a GUID whose prefab no longer exists, so
/// both give the same buffers, and this needs no live character. A buffer whose every row is stale
/// is left alone and warned about (a zero-row raw write is unverified). Strides and offsets come
/// from <see cref="Layout"/> and are cross-checked against the interop before the first write.</para>
///
/// <para><b>Status.</b> Source-checked against the server dump, compiled and unit-tested
/// (<c>UnlockPrunePlanTests</c>). Not run live.</para>
///
/// <para>Public only because <c>Progenitor.Dev</c> (the <c>unlock</c> op) reads
/// <see cref="TryDescribeGuid"/>; not a plugin API.</para>
/// </summary>
public static unsafe class UnlockPrune
{
    private static readonly object Gate = new();
    private static UnlockPrunePlan.Ledger? _ledger;
    private static bool _ledgerLoaded;
    private static IDisposable? _connectSub;
    private static bool _layoutChecked;
    private static bool _ledgerWritable = true;
    private static bool _saveLoadSweepQueued;

    /// <summary>
    /// Delay between the save load and the all-users sweep, so reloadable plugins have loaded
    /// (the reloader scans on frame ticks from process start).
    /// </summary>
    internal const double WorldLoadSweepDelaySeconds = 30;

    /// <summary>Server only. Called from <c>PrefabRegistry.Install</c>.</summary>
    internal static void Install()
    {
        if (!Plugin.IsServerProcess || _connectSub != null) return;
        try { _connectSub = EventBus.On<PlayerConnected>(OnPlayerConnected); }
        catch (Exception e) { Plugin.Logger?.LogError("UnlockPrune: PlayerConnected hook threw: " + e); }
    }

    /// <summary>Called from <c>PrefabRegistry.Uninstall</c>. The ledger file stays.</summary>
    internal static void Uninstall()
    {
        try { _connectSub?.Dispose(); } catch { }
        _connectSub = null;
        lock (Gate)
        {
            _ledger = null;
            _ledgerLoaded = false;
            _ledgerWritable = true;
        }
    }

    /// <summary>Server: the save was deserialized. Queue the all-users ledger sweep once per process.</summary>
    internal static void NoteSaveLoaded()
    {
        if (!Plugin.IsServerProcess || _saveLoadSweepQueued) return;
        _saveLoadSweepQueued = true;
        try
        {
            Dev.MainThread.PostDelayed(WorldLoadSweepDelaySeconds, () =>
            {
                try
                {
                    List<int> candidates;
                    lock (Gate) candidates = new List<int>(LedgerNoLock().Guids);
                    if (candidates.Count > 0) PruneAllUsers(Worlds.Server, candidates, "after world load", checkMods: true);
                }
                catch (Exception e) { Plugin.Logger?.LogError("UnlockPrune: world-load sweep threw: " + e); }
            });
        }
        catch (Exception e) { Plugin.Logger?.LogError("UnlockPrune: could not queue the world-load sweep: " + e); }
    }

    // ---- registration bookkeeping ---------------------------------------------------------------

    /// <summary>Server: remember that <paramref name="def"/>'s GUID is a Progenitor recipe and which mod owns it.</summary>
    internal static void NoteRegistered(RecipeDefinition def)
    {
        if (!Plugin.IsServerProcess) return;
        try
        {
            string mod = ModIdOf(def.Owner);
            lock (Gate)
            {
                var ledger = LedgerNoLock();
                if (!ledger.Note(def.GuidHash, def.Id, UnlockPrunePlan.KindRecipe, mod, DateTime.UtcNow)) return;
                if (_ledgerWritable) WriteLedgerNoLock(ledger);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"UnlockPrune: ledger note for '{def.Id}' failed: {e.Message}"); }
    }

    /// <summary>
    /// Server: <paramref name="defs"/> were just unregistered. Prune their GUIDs from every user on
    /// the next frame when <see cref="UnlockPrunePlan.ShouldPruneOnUnregister"/> says so.
    /// </summary>
    internal static void AfterUnregister(IReadOnlyList<RecipeDefinition> defs)
    {
        if (!Plugin.IsServerProcess || defs.Count == 0) return;
        var reason = PluginUnloadScope.Current;
        if (!UnlockPrunePlan.ShouldPruneOnUnregister(reason)) return;
        var guids = new List<int>(defs.Count);
        foreach (var d in defs) guids.Add(d.GuidHash);
        string trigger = reason == null ? "after unregister (handle disposed)" : "after unregister reason=" + reason;
        try
        {
            Dev.MainThread.Post(() =>
            {
                try { PruneAllUsers(Worlds.Server, guids, trigger, checkMods: false); }
                catch (Exception e) { Plugin.Logger?.LogError("UnlockPrune: prune after unregister threw: " + e); }
            });
        }
        catch (Exception e) { Plugin.Logger?.LogError("UnlockPrune: could not queue the prune: " + e); }
    }

    /// <summary>
    /// Which mod registered <paramref name="guidHash"/>: a loaded recipe first, then the persisted
    /// ledger. Used by the Dev <c>unlock</c> op to record ownership.
    /// </summary>
    public static bool TryDescribeGuid(int guidHash, out string mod, out string id, out string kind)
    {
        foreach (var def in RecipeApply.Definitions)
        {
            if (def.GuidHash != guidHash) continue;
            mod = ModIdOf(def.Owner);
            id = def.Id;
            kind = UnlockPrunePlan.KindRecipe;
            return true;
        }
        lock (Gate)
        {
            if (LedgerNoLock().TryGet(guidHash, out var e))
            {
                mod = e.Mod;
                id = e.Id;
                kind = e.Kind;
                return true;
            }
        }
        mod = id = kind = "";
        return false;
    }

    // ---- triggers -------------------------------------------------------------------------------

    private static void OnPlayerConnected(PlayerConnected ev)
    {
        List<int> candidates;
        lock (Gate)
        {
            var ledger = LedgerNoLock();
            if (ledger.Count == 0) return;
            candidates = new List<int>(ledger.Guids);
        }
        var world = ev.World;
        var user = ev.UserEntity;
        // Next frame: the connect postfix may run inside a system update.
        Dev.MainThread.Post(() =>
        {
            try
            {
                if (world == null || !world.IsCreated) return;
                var stale = StaleNow(world, candidates, checkMods: true);
                if (stale.Count == 0) return;
                CheckLayouts();
                PruneUser(world.EntityManager, user, stale, "on join");
            }
            catch (Exception e) { Plugin.Logger?.LogError("UnlockPrune: join sweep threw: " + e); }
        });
    }

    private static void PruneAllUsers(World? world, List<int> candidates, string trigger, bool checkMods)
    {
        if (world == null || !world.IsCreated) return;
        var stale = StaleNow(world, candidates, checkMods);
        if (stale.Count == 0) return;
        CheckLayouts();
        var em = world.EntityManager;
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        int touched = 0, failed = 0;
        foreach (var u in users)
        {
            // One user's failure must not leave the rest unpruned.
            try { if (PruneUser(em, u, stale, trigger)) touched++; }
            catch (Exception e)
            {
                failed++;
                Plugin.Logger?.LogError($"UnlockPrune: user {u.Index}:{u.Version} threw ({trigger}): {e.Message}");
            }
        }
        Plugin.Logger?.LogInfo($"UnlockPrune: {stale.Count} stale GUID(s) checked on {users.Count} user(s), {touched} changed, {failed} failed ({trigger})");
    }

    /// <summary>
    /// Candidates that no loaded plugin registers and that do not resolve to someone else's prefab;
    /// with <paramref name="checkMods"/>, also not registered by a mod this process still knows.
    /// </summary>
    private static HashSet<int> StaleNow(World world, List<int> candidates, bool checkMods)
    {
        var registered = new HashSet<int>();
        foreach (var d in RecipeApply.Definitions) registered.Add(d.GuidHash);
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        UnlockPrunePlan.Ledger? ledger = null;
        if (checkMods) lock (Gate) ledger = LedgerNoLock();
        return UnlockPrunePlan.Stale(candidates, registered, g =>
        {
            if (prefabs == null) return true; // cannot tell: never prune blind
            if (ledger != null && ledger.TryGet(g, out var row) && ModStillPresent(row.Mod)) return true;
            if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(g), out var e) || e == Entity.Null) return false;
            return !RecipeApply.IsClonedHere(g);
        });
    }

    private static bool ModStillPresent(string mod)
    {
        bool known = ModRegistry.TryGet(mod, out var entry);
        return UnlockPrunePlan.ModStillPresent(known, known && entry.Outcome == ModLoadOutcome.Unloaded);
    }

    // ---- ECS -----------------------------------------------------------------------------------

    /// <summary>True when any buffer of this user changed.</summary>
    private static bool PruneUser(EntityManager em, Entity user, HashSet<int> stale, string trigger)
    {
        if (user == Entity.Null || !em.Exists(user)) return false;
        string name;
        try { name = em.GetComponentData<User>(user).CharacterName.ToString(); } // bool-bearing: the typed accessor
        catch { name = $"{user.Index}:{user.Version}"; }
        if (string.IsNullOrEmpty(name)) name = $"{user.Index}:{user.Version}";

        var mapper = ComponentIndex.Of<ProgressionMapper>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(user, mapper)) return false;
        byte* raw = (byte*)em.GetComponentDataRawRO(user, mapper.TypeIndex);
        if (raw == null) return false;
        if (raw[Layout.NetworkedEntityWaitingForSyncOffset] != 0)
        {
            Plugin.Logger?.LogWarning($"UnlockPrune: '{name}' progression is waiting for sync; not pruned ({trigger})");
            return false;
        }
        var progression = *(Entity*)(raw + Layout.ProgressionMapperEntityOffset);
        if (progression == Entity.Null || !em.Exists(progression)) return false;

        // Each buffer on its own: one failure is logged and does not stop the others.
        var removedProgression = SafeFilter(em, progression, ComponentIndex.Of<UnlockedProgressionElement>(ComponentType.AccessMode.ReadWrite),
            Layout.UnlockedProgressionElementStride, Layout.UnlockedProgressionElementGuidOffset, stale, name, "UnlockedProgressionElement");
        var removedRecipes = SafeFilter(em, progression, ComponentIndex.Of<UnlockedRecipeElement>(ComponentType.AccessMode.ReadWrite),
            Layout.UnlockedRecipeElementStride, Layout.UnlockedRecipeElementGuidOffset, stale, name, "UnlockedRecipeElement");
        var removedBlueprints = SafeFilter(em, progression, ComponentIndex.Of<UnlockedBlueprintElement>(ComponentType.AccessMode.ReadWrite),
            Layout.UnlockedBlueprintElementStride, Layout.UnlockedBlueprintElementGuidOffset, stale, name, "UnlockedBlueprintElement");
        if (removedProgression.Count + removedRecipes.Count + removedBlueprints.Count == 0) return false;

        UnlockPrunePlan.Ledger? ledger;
        lock (Gate) ledger = LedgerNoLock();
        Plugin.Logger?.LogInfo(UnlockPrunePlan.FormatPruned(name, removedProgression, removedRecipes, removedBlueprints,
            g => UnlockPrunePlan.Describe(ledger, g), trigger));
        return true;
    }

    private static List<int> SafeFilter(EntityManager em, Entity entity, ComponentType type, int stride, int guidOffset,
        HashSet<int> stale, string user, string label)
    {
        try { return FilterBuffer(em, entity, type, stride, guidOffset, stale, user, label); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"UnlockPrune: {label} of '{user}' not pruned: {e.Message}");
            return new List<int>();
        }
    }

    /// <summary>Drop the stale rows of one buffer in one raw write; returns the GUIDs removed.</summary>
    private static List<int> FilterBuffer(EntityManager em, Entity entity, ComponentType type, int stride, int guidOffset,
        HashSet<int> stale, string user, string label)
    {
        var none = new List<int>();
        if (!em.HasComponent(entity, type)) return none;
        int n = em.GetBufferLength(entity, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(entity, type.TypeIndex);
        if (n == 0 || raw == null) return none;

        var guids = new int[n];
        for (int i = 0; i < n; i++) guids[i] = *(int*)(raw + i * stride + guidOffset);
        var filter = UnlockPrunePlan.Filter(guids, stale);
        if (!filter.Changed) return none;
        if (filter.KeptRows.Count == 0)
        {
            Plugin.Logger?.LogWarning($"UnlockPrune: every {label} row of '{user}' is stale ({n}); left as is (a zero-row write is unverified)");
            return none;
        }

        int kept = filter.KeptRows.Count;
        var data = new byte[kept * stride];
        fixed (byte* dst = data)
        {
            for (int k = 0; k < kept; k++)
            {
                byte* src = raw + filter.KeptRows[k] * stride;
                for (int b = 0; b < stride; b++) dst[k * stride + b] = src[b];
            }
            // raw is not used after this call: the write may move the buffer.
            UnsafeEntityManagerUtility.SetBufferDataRaw(em, entity, type.TypeIndex.Value, stride, 4, kept, dst);
        }

        int after = em.GetBufferLength(entity, type.TypeIndex);
        byte* back = (byte*)em.GetBufferRawRO(entity, type.TypeIndex);
        bool same = after == kept && back != null;
        for (int i = 0; same && i < after; i++)
            same = !stale.Contains(*(int*)(back + i * stride + guidOffset));
        if (!same) throw new InvalidOperationException($"{label} write for '{user}' did not read back ({after} rows, expected {kept})");
        return filter.Removed;
    }

    /// <summary>Refuse every write unless the interop agrees with the dump layout the strides assume.</summary>
    private static void CheckLayouts()
    {
        if (_layoutChecked) return;
        Expect(Marshal.SizeOf<UnlockedProgressionElement>() == Layout.UnlockedProgressionElementStride, "UnlockedProgressionElement size");
        Expect((int)Marshal.OffsetOf<UnlockedProgressionElement>(nameof(UnlockedProgressionElement.UnlockedPrefab)) == Layout.UnlockedProgressionElementGuidOffset, "UnlockedProgressionElement.UnlockedPrefab offset");
        Expect((int)Marshal.OffsetOf<UnlockedRecipeElement>(nameof(UnlockedRecipeElement.UnlockedRecipe)) == Layout.UnlockedRecipeElementGuidOffset, "UnlockedRecipeElement.UnlockedRecipe offset");
        Expect((int)Marshal.OffsetOf<UnlockedRecipeElement>(nameof(UnlockedRecipeElement.UserHasRequiredContentFlags)) < Layout.UnlockedRecipeElementStride, "UnlockedRecipeElement flag inside the stride");
        Expect((int)Marshal.OffsetOf<UnlockedBlueprintElement>(nameof(UnlockedBlueprintElement.UnlockedBlueprint)) == Layout.UnlockedBlueprintElementGuidOffset, "UnlockedBlueprintElement.UnlockedBlueprint offset");
        Expect((int)Marshal.OffsetOf<UnlockedBlueprintElement>(nameof(UnlockedBlueprintElement.UserHasRequiredContentFlags)) < Layout.UnlockedBlueprintElementStride, "UnlockedBlueprintElement flag inside the stride");
        _layoutChecked = true;
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("UnlockPrune: " + what + " disagrees with the dump layout; refusing the raw write");
    }

    // ---- ledger file ---------------------------------------------------------------------------

    private static string LedgerPath => Path.Combine(Paths.ConfigPath, UnlockPrunePlan.LedgerFileName);

    private static UnlockPrunePlan.Ledger LedgerNoLock()
    {
        if (_ledgerLoaded && _ledger != null) return _ledger;
        string path = LedgerPath;
        try
        {
            _ledger = File.Exists(path) ? UnlockPrunePlan.Ledger.Parse(File.ReadAllText(path)) : new UnlockPrunePlan.Ledger();
            _ledgerWritable = true;
            _ledgerLoaded = true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            // Corrupt: keep it under a new name and start over; never write over a file we could not move.
            string bad = path + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture) + ".bad";
            bool moved = false;
            try { File.Move(path, bad); moved = true; } catch { }
            Plugin.Logger?.LogWarning($"UnlockPrune: {path} is not a valid ledger ({e.Message}); "
                + (moved ? $"moved to {bad}, starting an empty one" : "could not move it, so it is not written this session"));
            _ledger = new UnlockPrunePlan.Ledger();
            _ledgerWritable = moved;
            _ledgerLoaded = true;
        }
        catch (Exception e)
        {
            // Unreadable right now (sharing, access): never overwrite it, and read it again next time.
            Plugin.Logger?.LogWarning($"UnlockPrune: {path} could not be read ({e.Message}); not writing it, will retry");
            _ledger = new UnlockPrunePlan.Ledger();
            _ledgerWritable = false;
            _ledgerLoaded = false;
        }
        return _ledger;
    }

    private static void WriteLedgerNoLock(UnlockPrunePlan.Ledger ledger)
    {
        string path = LedgerPath;
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ledger.Serialize());
        File.Move(tmp, path, overwrite: true);
    }

    private static string ModIdOf(object owner)
    {
        try
        {
            if (owner is BasePlugin plugin) return MetadataHelper.GetMetadata(plugin).GUID;
        }
        catch { }
        return owner?.GetType().FullName ?? "";
    }
}
