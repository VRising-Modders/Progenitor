using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Il2CppInterop.Runtime;
using Progenitor.Access;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Hooks;

/// <summary>
/// Prefix and postfix hooks on any ECS system's update, managed or unmanaged, with the same API.
///
/// Unity Entities 1.x funnels managed systems through <see cref="SystemBase.Update"/> and unmanaged
/// systems through <see cref="WorldUnmanagedImpl.UnmanagedUpdate"/>. Both sit in front of the
/// enabled / <c>ShouldRunSystem</c> gate, so a Harmony prefix returning false skips that system for
/// the frame. The system is identified from <see cref="SystemState.m_SystemTypeIndex"/>; we never
/// put a by-ref <see cref="SystemState"/> in a Harmony signature (Harmony cannot marshal those on
/// IL2CPP methods called from native). Managed state is read from <c>ComponentSystemBase.m_StatePtr</c>
/// instead of <c>CheckedStateRef</c> for the same reason.
///
/// Registrations return <see cref="IDisposable"/> so a reloadable plugin can undo them in Unload.
/// Call <see cref="Install"/> once from Core; installing twice is a no-op. Per-site Before/After
/// order is numeric priority (higher first), then Harmony-style before/after plugin ids, then
/// plugin load order, then registration order; a cycle drops only the registration that would
/// close it (acyclic edges at the site stay). <see cref="RemoveAll"/> re-resolves only sites
/// that lost a hook. See <c>docs/hooks-ordering.md</c>.
///
/// The Harmony prefixes run for every system every frame. An immutable
/// <see cref="HookedIndexSet"/> of hooked type indices is rebuilt on registration change and
/// published with <c>Volatile.Write</c>; the unhooked path is then a snapshot read plus
/// <see cref="HookedIndexSet.Contains"/> — no lock, no allocation, no <c>GetType</c>. Before/After
/// handler arrays are copied at the same time so a hooked system does not <c>ToArray</c> per update.
/// </summary>
public static unsafe class SystemHooks
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, Bucket> ByIndex = new();
    private static readonly Dictionary<Type, int> IndexByType = new();
    private static readonly List<Entry> Unresolved = new();
    private static readonly HashSet<Type> Reported = new();
    private static readonly List<WorldWaiter> Waiters = new();
    private static readonly HookCallStack<CallFrame> Frames = new(8);
    private static readonly Dictionary<int, HandlerPair> NoHandlers = new();

    private static bool _installed;
    private static bool _usedFallback;
    private static bool _frameHooked;
    private static int _skipMisuseLogged;
    private static int _abandonedLogged;
    private static Snapshot _snapshot = Snapshot.Empty;
    private static int _nextIndex;
    private static bool _systemTableReady;

    /// <summary>
    /// Run <paramref name="hook"/> before <typeparamref name="TSystem"/> updates. Dispose to remove
    /// it; the Harmony patches stay, they just stop calling this delegate. Ordering fields on
    /// <paramref name="options"/> (<see cref="HookOptions.Priority"/>, Before/After plugin ids)
    /// are why two plugins can share a system without fighting over an integer.
    /// </summary>
    public static IDisposable Before<TSystem>(Action<SystemHookContext> hook, HookOptions? options = null)
        => Before(typeof(TSystem), hook, options);

    /// <summary>
    /// Run <paramref name="hook"/> after <typeparamref name="TSystem"/> updates. Dispose to remove it.
    /// </summary>
    public static IDisposable After<TSystem>(Action<SystemHookContext> hook, HookOptions? options = null)
        => After(typeof(TSystem), hook, options);

    /// <summary>Non-generic Before. <paramref name="systemType"/> is a game SystemBase or ISystem type.</summary>
    public static IDisposable Before(Type systemType, Action<SystemHookContext> hook, HookOptions? options = null)
        => Add(systemType, hook, options, before: true);

    /// <summary>Non-generic After. <paramref name="systemType"/> is a game SystemBase or ISystem type.</summary>
    public static IDisposable After(Type systemType, Action<SystemHookContext> hook, HookOptions? options = null)
        => Add(systemType, hook, options, before: false);

    /// <summary>
    /// Before with explicit Harmony-style ordering. Higher <paramref name="priority"/> runs first.
    /// <paramref name="before"/> / <paramref name="after"/> are plugin ids; a cycle is refused with
    /// one Warn line and that registration is dropped. <paramref name="pluginId"/> is this
    /// plugin's BepInEx GUID so others can name it and so equal-priority order is load-order
    /// stable across a plugin reload.
    /// </summary>
    public static IDisposable Before<TSystem>(
        Action<SystemHookContext> hook,
        int priority,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        HookOptions? options = null)
        => Before(typeof(TSystem), hook, priority, before, after, pluginId, options);

    /// <summary>
    /// After with explicit Harmony-style ordering. Same rules as the Before overload.
    /// </summary>
    public static IDisposable After<TSystem>(
        Action<SystemHookContext> hook,
        int priority,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        HookOptions? options = null)
        => After(typeof(TSystem), hook, priority, before, after, pluginId, options);

    /// <summary>Non-generic Before with explicit ordering. <paramref name="systemType"/> is a game SystemBase or ISystem type.</summary>
    public static IDisposable Before(
        Type systemType,
        Action<SystemHookContext> hook,
        int priority,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        HookOptions? options = null)
        => Add(systemType, hook, options, before: true, priority, before, after, pluginId);

    /// <summary>Non-generic After with explicit ordering. <paramref name="systemType"/> is a game SystemBase or ISystem type.</summary>
    public static IDisposable After(
        Type systemType,
        Action<SystemHookContext> hook,
        int priority,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null,
        HookOptions? options = null)
        => Add(systemType, hook, options, before: false, priority, before, after, pluginId);

    /// <summary>
    /// Invoke <paramref name="callback"/> once when a world of this name exists. Fires immediately
    /// if it is already created, otherwise from Core's per-frame tick. Dispose cancels a wait that
    /// has not fired yet. Used to defer bootstrap work that needs the Server or Client world.
    /// </summary>
    public static IDisposable WhenWorldExists(string worldName, Action<World> callback)
    {
        if (string.IsNullOrEmpty(worldName))
            throw new ArgumentException("World name is required.", nameof(worldName));
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        var waiter = new WorldWaiter(worldName, callback);
        lock (Gate) Waiters.Add(waiter);
        EnsureFrameHook();
        DeliverWaiters();
        return waiter;
    }

    /// <summary>
    /// Patches <see cref="SystemBase.Update"/> and <see cref="WorldUnmanagedImpl.UnmanagedUpdate"/>
    /// (or fills Bootstrap slots when those patches already live in Bootstrap). A second call
    /// is a no-op. <paramref name="harmony"/> is the fallback Harmony instance
    /// when Bootstrap is absent; production passes null and fills slots.
    /// </summary>
    public static void Install(object? harmony)
    {
        _ = harmony;
        lock (Gate)
        {
            if (_installed) return;

            if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.HooksModule))
            {
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.HooksModule));
                return;
            }

            if (HarmonySlotHandoff.SkipCoreHarmony)
            {
                _installed = true;
                EnsureFrameHook();
                Plugin.Logger?.LogInfo("SystemHooks: installed (Harmony skipped).");
                return;
            }

            bool managedSlots = TryFillManagedSlots();
            bool unmanagedSlots = TryFillUnmanagedSlots();

            bool managedArmed = managedSlots
                || TryFallbackOrLogVanilla(HarmonySlotHandoff.SystemBaseUpdate, managed: true);
            bool unmanagedArmed = unmanagedSlots
                || TryFallbackOrLogVanilla(HarmonySlotHandoff.UnmanagedUpdate, managed: false);

            _installed = true;
            EnsureFrameHook();
            // Job FHARM8 (R42 finding 5). This line used to be unconditional, so a Refused route
            // — Bootstrap present but a version behind, which CorePatchFallbackPolicy calls the
            // ordinary case, not an exotic one — left "installed Harmony patches on ..." as the
            // last word in the log while neither patch had been applied. An operator grepping
            // `SystemHooks:` found a success. Report what actually landed.
            Plugin.Logger?.LogInfo(FormatInstalled(managedArmed, unmanagedArmed));
            if (!managedArmed || !unmanagedArmed)
            {
                Plugin.Logger?.LogError(
                    "SystemHooks: system Before/After hooks are OFF for "
                    + (managedArmed || unmanagedArmed
                        ? (managedArmed ? "unmanaged systems" : "managed systems")
                        : "every system")
                    + " for this Core generation; see the patches: line above for why.");
            }
        }
    }

    /// <summary>
    /// The install line, naming which of the two seams actually armed (job FHARM8, R42 finding 5).
    /// </summary>
    internal static string FormatInstalled(bool managedArmed, bool unmanagedArmed) =>
        managedArmed && unmanagedArmed
            ? "SystemHooks: installed Harmony patches on SystemBase.Update and WorldUnmanagedImpl.UnmanagedUpdate."
            : managedArmed
                ? "SystemHooks: installed on SystemBase.Update ONLY; WorldUnmanagedImpl.UnmanagedUpdate is NOT patched."
                : unmanagedArmed
                    ? "SystemHooks: installed on WorldUnmanagedImpl.UnmanagedUpdate ONLY; SystemBase.Update is NOT patched."
                    : "SystemHooks: installed NOTHING; neither SystemBase.Update nor WorldUnmanagedImpl.UnmanagedUpdate is patched.";

    /// <summary>True when the site ended up armed (a fallback patch actually landed).</summary>
    private static bool TryFallbackOrLogVanilla(string site, bool managed)
    {
        bool owns = HarmonySlotHandoff.HostOwnsSite(site);
        if (HarmonySlotHandoff.CoreMayApplyHarmony(owns, HarmonySlotHandoff.SkipCoreHarmony))
        {
            bool ok = managed
                ? Reload.HarmonyFallback.TryPatchManagedSystemUpdate()
                : Reload.HarmonyFallback.TryPatchUnmanagedSystemUpdate();
            if (ok) _usedFallback = true;
            return ok;
        }
        if (owns)
        {
            Plugin.Logger?.LogError(
                "SystemHooks: Bootstrap owns " + site + " but slot write failed; staying inert for this site (no second patch).");
            return false;
        }
        Plugin.Logger?.LogError(
            "SystemHooks: " + site + " is vanilla (Bootstrap did not patch it and Core cannot fallback).");
        return false;
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (Plugin.Frame waiter, Harmony fallback, handler tables).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_installed) holds.Add("installed");
            if (_frameHooked) holds.Add("Plugin.Frame");
            if (_usedFallback) holds.Add("HarmonyFallback");
            holds.Add(DescribeCounts());
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.HooksModule, holds);
    }

    /// <summary>Handler / waiter counts after plugin sweep, for the Core-reload pin dump.</summary>
    internal static string DescribeCounts()
    {
        lock (Gate)
        {
            int before = 0, after = 0;
            foreach (var kv in ByIndex)
            {
                before += kv.Value.Before.Count;
                after += kv.Value.After.Count;
            }
            return "before=" + before
                + " after=" + after
                + " unresolved=" + Unresolved.Count
                + " waiters=" + Waiters.Count
                // Job FLATCH1: a non-zero abandoned count is a system throwing under a patched
                // Update; each one held a World and a plugin's handler array until the sweep.
                //
                // Job FLATCH2: this number is deliberately NOT reset by HookCallStack.Clear, and
                // that is the asymmetry with Bootstrap's AfterCallStack, which is. The two are
                // read for different purposes. Bootstrap's total feeds a latched warning line, so
                // a stale total makes that line report a strand the generation never had (R45
                // finding 14). This one feeds a pin dump, where the wanted quantity is "how many
                // frames has this ALC ever stranded" - the warning above is computed from the
                // just-measured sweep result instead, so no report here can go stale.
                + " frames=" + Frames.Depth
                + " abandoned=" + Frames.AbandonedTotal
                + " installed=" + _installed;
        }
    }

    /// <summary>
    /// Removes the two Harmony patches and the <see cref="Plugin.Frame"/> waiter hook so a
    /// collectible Core Uninstall does not leave a subscriber pointing at this generation.
    /// Hook registrations stay until <see cref="RemoveAll"/> or Dispose.
    /// </summary>
    public static void Uninstall(object? harmony)
    {
        _ = harmony;
        lock (Gate)
        {
            if (_frameHooked)
            {
                try { Plugin.Frame -= OnFrame; } catch { /* Frame may already be null */ }
                _frameHooked = false;
            }
            // Job FLATCH1: the backstop for a frame stranded by a throwing system after the last
            // OnFrame sweep. Nothing may cross Uninstall holding a World or a handler array;
            // Bootstrap does the same with HarmonyPatches.ClearAfterSnapshots. Ahead of the
            // not-installed return: the sweep above has just gone away either way.
            Frames.Clear();
            // Job FLATCH2 (R45 finding 15). The latch re-arms on the same line that clears the
            // thing it describes. This is the twin of HarmonyPatches.ClearAfterSnapshots, and it
            // is NOT dead just because Core statics normally die with the collectible ALC: the
            // reachable path is CoreLoader's restore-in-place. When the collect is refused
            // (leftover Harmony patches, receive in-flight, a plugin ALC still alive) the
            // orchestrator does not load a new image - LiveOps.ReinstallCurrent re-Installs THIS
            // assembly in THIS ALC, so every static here survives an Uninstall/Install pair.
            // Without the re-arm the restored installation starts with the flag already spent:
            // the one line saying a system is throwing under a patched Update was consumed by the
            // installation that was just torn down, and every strand afterwards is silent.
            Interlocked.Exchange(ref _abandonedLogged, 0);
            if (!_installed) return;
            // Always clear, even if TryFill was partial and _usedSlots stayed false.
            HarmonySlotHandoff.Clear(HarmonySlotHandoff.SystemBaseUpdatePrefix);
            HarmonySlotHandoff.Clear(HarmonySlotHandoff.SystemBaseUpdatePostfix);
            HarmonySlotHandoff.Clear(HarmonySlotHandoff.UnmanagedUpdatePrefix);
            HarmonySlotHandoff.Clear(HarmonySlotHandoff.UnmanagedUpdatePostfix);
            if (_usedFallback)
            {
                Reload.HarmonyFallback.UnpatchSystemUpdates();
                _usedFallback = false;
            }
            _installed = false;
            Plugin.Logger?.LogInfo("SystemHooks: Harmony patches removed.");
        }
    }

    /// <summary>
    /// Drop Before/After hooks and world waiters whose target or declaring assembly matches
    /// <paramref name="owner"/>. Reload sweep uses this so a leftover hook cannot pin a
    /// collectible plugin ALC after Unload threw.
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) return 0;
        var asm = owner as Assembly ?? owner.GetType().Assembly;
        int n = 0;
        lock (Gate)
        {
            int unresolvedRemoved = RemoveMatching(Unresolved, owner, asm);
            n += unresolvedRemoved;
            var empty = new List<int>();
            var dirty = new List<(int index, bool before, string name)>();
            foreach (var kv in ByIndex)
            {
                int beforeRemoved = RemoveMatching(kv.Value.Before, owner, asm);
                int afterRemoved = RemoveMatching(kv.Value.After, owner, asm);
                n += beforeRemoved + afterRemoved;
                if (kv.Value.Before.Count == 0 && kv.Value.After.Count == 0)
                {
                    empty.Add(kv.Key);
                    continue;
                }
                string name = SystemNameOf(kv.Value);
                if (HookOrder.PhaseNeedsResort(beforeRemoved, kv.Value.Before.Count))
                    dirty.Add((kv.Key, true, name));
                if (HookOrder.PhaseNeedsResort(afterRemoved, kv.Value.After.Count))
                    dirty.Add((kv.Key, false, name));
            }
            for (int i = 0; i < empty.Count; i++)
                ByIndex.Remove(empty[i]);
            n += RemoveMatchingWaiters(owner, asm);
            for (int i = 0; i < dirty.Count; i++)
            {
                var site = dirty[i];
                SortBucketPhase(site.index, site.before, site.name);
            }
            if (unresolvedRemoved > 0)
                ResortRemainingUnresolved();
            PublishSnapshot_NoLock();
        }
        return n;
    }

    private static string SystemNameOf(Bucket bucket)
    {
        if (bucket.Before.Count > 0) return bucket.Before[0].SystemName;
        if (bucket.After.Count > 0) return bucket.After[0].SystemName;
        return "";
    }

    private static int RemoveMatching(List<Entry> list, object owner, Assembly? asm)
    {
        int n = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var entry = list[i];
            if (!DelegateOwnedBy(entry.Hook, owner, asm)) continue;
            entry.Removed = true;
            list.RemoveAt(i);
            n++;
        }
        return n;
    }

    private static int RemoveMatchingWaiters(object owner, Assembly? asm)
    {
        int n = 0;
        for (int i = Waiters.Count - 1; i >= 0; i--)
        {
            if (!DelegateOwnedBy(Waiters[i].Callback, owner, asm)) continue;
            Waiters.RemoveAt(i);
            n++;
        }
        return n;
    }

    private static bool DelegateOwnedBy(Delegate d, object owner, Assembly? asm)
    {
        if (d == null) return false;
        if (ReferenceEquals(d.Target, owner)) return true;
        if (asm == null) return false;
        if (d.Method.DeclaringType?.Assembly == asm) return true;
        return d.Target != null && d.Target.GetType().Assembly == asm;
    }

    internal static void WarnSkipFromAfter(string systemName)
    {
        if (Interlocked.Exchange(ref _skipMisuseLogged, 1) != 0) return;
        Plugin.Logger?.LogWarning($"SystemHooks: Skip is only settable in Before hooks ({systemName}); ignoring.");
    }

    /// <summary>
    /// Resolved Before/After order for <typeparamref name="TSystem"/> as a multi-line dump a
    /// future <c>hooks_order</c> op can print.
    /// </summary>
    public static string DescribeOrder<TSystem>() => DescribeOrder(typeof(TSystem));

    /// <summary>
    /// Resolved Before/After order for <paramref name="site"/> (a game system type). Unknown
    /// types dump empty Before/After blocks rather than throwing, so a diagnostic op can
    /// probe any name.
    /// </summary>
    public static string DescribeOrder(Type site)
    {
        if (site == null) throw new ArgumentNullException(nameof(site));
        return DescribeOrder(site, site.Name);
    }

    /// <summary>
    /// Resolved order for a system identified by type name (or <see cref="Type.FullName"/>).
    /// Matches the system's <see cref="Type.Name"/> first, then full name.
    /// </summary>
    public static string DescribeOrder(string site)
    {
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("Site is required.", nameof(site));
        Type? match = null;
        string name = site;
        lock (Gate)
        {
            foreach (var kv in IndexByType)
            {
                if (NamesMatch(kv.Key, site))
                {
                    match = kv.Key;
                    name = kv.Key.Name;
                    break;
                }
            }
            if (match == null)
            {
                for (int i = 0; i < Unresolved.Count; i++)
                {
                    if (!NamesMatch(Unresolved[i].SystemType, site)
                        && !string.Equals(Unresolved[i].SystemName, site, StringComparison.Ordinal))
                        continue;
                    match = Unresolved[i].SystemType;
                    name = Unresolved[i].SystemName;
                    break;
                }
            }
        }
        return DescribeOrder(match, name);
    }

    // ---- registration -------------------------------------------------------------------------

    private static IDisposable Add(
        Type systemType,
        Action<SystemHookContext> hook,
        HookOptions? options,
        bool before,
        int priority = 0,
        string[]? beforeIds = null,
        string[]? afterIds = null,
        string? pluginId = null)
    {
        if (systemType == null) throw new ArgumentNullException(nameof(systemType));
        if (hook == null) throw new ArgumentNullException(nameof(hook));

        // Il2CppType.From talks to the class pointer store, not TypeManager, so a missing IL2CPP
        // wrapper fails now rather than as a silent no-op later. TypeManager itself may not be
        // initialized yet; that case is deferred, not thrown.
        if (Il2CppType.From(systemType, throwOnFailure: false) == null)
            throw new ArgumentException($"'{systemType.FullName}' has no IL2CPP class pointer; pass a game SystemBase or ISystem type.", nameof(systemType));

        if (priority == 0 && options != null) priority = options.Priority;
        beforeIds ??= options?.Before;
        afterIds ??= options?.After;
        pluginId ??= options?.PluginId;

        var entry = new Entry
        {
            SystemType = systemType,
            SystemName = systemType.Name,
            Hook = hook,
            Before = before,
            OnlyWhenSystemRuns = options?.OnlyWhenSystemRuns ?? true,
            EveryNFrames = options is { EveryNFrames: > 1 } ? options.EveryNFrames : 1,
            Priority = priority,
            PluginId = pluginId ?? "",
            BeforeIds = OrderSorter.CopyIds(beforeIds),
            AfterIds = OrderSorter.CopyIds(afterIds),
            LoadOrder = OrderSorter.AssignLoadOrder(pluginId),
        };

        string? cycleLog = null;
        bool accepted = false;
        lock (Gate)
        {
            entry.Index = _nextIndex++;
            int index = ResolveIndex(systemType);
            if (index == 0)
                cycleLog = AcceptUnresolved(entry, out accepted);
            else
                cycleLog = AcceptPlaced(entry, index, out accepted);
            if (accepted)
                PublishSnapshot_NoLock();
        }
        if (cycleLog != null)
            Plugin.Logger?.LogWarning(cycleLog);

        if (accepted)
            EnsureFrameHook();
        return new Registration(entry);
    }

    private static void Place(Entry entry, int index)
    {
        if (!ByIndex.TryGetValue(index, out var bucket))
        {
            bucket = new Bucket();
            ByIndex[index] = bucket;
        }
        if (entry.Before) bucket.Before.Add(entry);
        else bucket.After.Add(entry);
        IndexByType[entry.SystemType] = index;
        entry.TypeIndex = index;
    }

    private static void Remove(Entry entry)
    {
        lock (Gate)
        {
            if (entry.Removed) return;
            entry.Removed = true;
            if (entry.TypeIndex != 0 && ByIndex.TryGetValue(entry.TypeIndex, out var bucket))
            {
                if (entry.Before) bucket.Before.Remove(entry);
                else bucket.After.Remove(entry);
                if (bucket.Before.Count == 0 && bucket.After.Count == 0)
                    ByIndex.Remove(entry.TypeIndex);
                else
                    SortBucketPhase(entry.TypeIndex, entry.Before, entry.SystemName);
            }
            else
            {
                Unresolved.Remove(entry);
                SortUnresolvedPhase(entry.SystemType, entry.Before);
            }
            PublishSnapshot_NoLock();
        }
    }

    private static string? AcceptPlaced(Entry entry, int index, out bool accepted)
    {
        List<Entry> list;
        if (ByIndex.TryGetValue(index, out var bucket))
            list = entry.Before ? bucket.Before : bucket.After;
        else
            list = new List<Entry>();

        if (!TryAcceptCandidate(list, entry, entry.SystemName, entry.Before ? "Before" : "After", out var cycleLog, out var indices))
        {
            accepted = false;
            return cycleLog;
        }

        Place(entry, index);
        if (ByIndex.TryGetValue(index, out bucket))
            list = entry.Before ? bucket.Before : bucket.After;
        OrderSorter.ApplyPermutation(list, indices);
        accepted = true;
        return null;
    }

    private static string? AcceptUnresolved(Entry entry, out bool accepted)
    {
        var subset = CollectUnresolved(entry.SystemType, entry.Before);
        if (!TryAcceptCandidate(subset, entry, entry.SystemName, entry.Before ? "Before" : "After", out var cycleLog, out var indices))
        {
            accepted = false;
            return cycleLog;
        }

        Unresolved.Add(entry);
        subset.Add(entry);
        OrderSorter.ApplyPermutation(subset, indices);
        WriteUnresolved(entry.SystemType, entry.Before, subset);
        accepted = true;
        return null;
    }

    private static bool TryAcceptCandidate(
        List<Entry> existing,
        Entry candidate,
        string systemName,
        string phase,
        out string? cycleLog,
        out int[] indices)
    {
        cycleLog = null;
        var specs = SpecsOf(existing);
        if (!OrderSorter.TryAccept(specs, candidate.ToSpec(), higherPriorityFirst: true, out indices, out var dropped, out var cycleIds))
        {
            candidate.Removed = true;
            cycleLog = HookOrder.CycleDropped(systemName, phase, dropped, cycleIds ?? Array.Empty<string>());
            return false;
        }
        return true;
    }

    private static void SortBucketPhase(int typeIndex, bool beforePhase, string systemName)
    {
        _ = systemName;
        if (!ByIndex.TryGetValue(typeIndex, out var bucket)) return;
        var list = beforePhase ? bucket.Before : bucket.After;
        ResortList(list);
    }

    private static void SortUnresolvedPhase(Type systemType, bool beforePhase)
    {
        var subset = CollectUnresolved(systemType, beforePhase);
        if (subset.Count <= 1) return;
        ResortList(subset);
        WriteUnresolved(systemType, beforePhase, subset);
    }

    private static void ResortRemainingUnresolved()
    {
        var seen = new HashSet<(Type type, bool before)>();
        for (int i = 0; i < Unresolved.Count; i++)
        {
            var entry = Unresolved[i];
            if (!seen.Add((entry.SystemType, entry.Before))) continue;
            SortUnresolvedPhase(entry.SystemType, entry.Before);
        }
    }

    private static List<Entry> CollectUnresolved(Type systemType, bool beforePhase)
    {
        var subset = new List<Entry>();
        for (int i = 0; i < Unresolved.Count; i++)
        {
            if (Unresolved[i].SystemType == systemType && Unresolved[i].Before == beforePhase)
                subset.Add(Unresolved[i]);
        }
        return subset;
    }

    private static void WriteUnresolved(Type systemType, bool beforePhase, List<Entry> subset)
    {
        int w = 0;
        for (int i = 0; i < Unresolved.Count; i++)
        {
            if (Unresolved[i].SystemType != systemType || Unresolved[i].Before != beforePhase)
                continue;
            Unresolved[i] = subset[w++];
        }
    }

    private static void ResortList(List<Entry> list)
    {
        if (list == null || list.Count <= 1) return;
        var specs = SpecsOf(list);
        if (!OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out _, out _))
            return;
        OrderSorter.ApplyPermutation(list, indices);
    }

    private static OrderSpec[] SpecsOf(List<Entry> list)
    {
        var specs = new OrderSpec[list.Count];
        for (int i = 0; i < list.Count; i++)
            specs[i] = list[i].ToSpec();
        return specs;
    }

    private static bool NamesMatch(Type type, string site)
    {
        if (type == null || string.IsNullOrEmpty(site)) return false;
        if (string.Equals(type.Name, site, StringComparison.Ordinal)) return true;
        return string.Equals(type.FullName, site, StringComparison.Ordinal);
    }

    private static string DescribeOrder(Type? site, string systemName)
    {
        OrderSpec[] before;
        OrderSpec[] after;
        lock (Gate)
        {
            CollectSpecsNoLock(site, systemName, out before, out after);
        }
        return HookOrder.Describe(systemName, before, after);
    }

    private static void CollectSpecsNoLock(Type? site, string systemName, out OrderSpec[] before, out OrderSpec[] after)
    {
        var beforeList = new List<OrderSpec>();
        var afterList = new List<OrderSpec>();
        if (site != null && IndexByType.TryGetValue(site, out int index) && ByIndex.TryGetValue(index, out var bucket))
        {
            AppendSpecs(beforeList, bucket.Before);
            AppendSpecs(afterList, bucket.After);
        }
        for (int i = 0; i < Unresolved.Count; i++)
        {
            var entry = Unresolved[i];
            bool match = site != null
                ? entry.SystemType == site
                : string.Equals(entry.SystemName, systemName, StringComparison.Ordinal);
            if (!match) continue;
            if (entry.Before) beforeList.Add(entry.ToSpec());
            else afterList.Add(entry.ToSpec());
        }
        before = beforeList.ToArray();
        after = afterList.ToArray();
    }

    private static void AppendSpecs(List<OrderSpec> dst, List<Entry> src)
    {
        for (int i = 0; i < src.Count; i++)
            dst.Add(src[i].ToSpec());
    }

    private static int ResolveIndex(Type systemType)
    {
        if (IndexByType.TryGetValue(systemType, out int cached) && cached > 0) return cached;
        // Before the first world initializes TypeManager the native lookup dereferences a null
        // table and throws (every client session: Core registers UI hooks at the title screen).
        // That is "not yet", not an error; the frame handler retries. See SystemTypeIndexLookup.
        if (!SystemTableReady()) return 0;
        try
        {
            var il2cppType = Il2CppType.From(systemType, throwOnFailure: false);
            if (il2cppType == null) return 0;
            // Non-generic on purpose: GetSystemTypeIndex<T>() for an ISystem struct is a generic
            // interop call over a game value type and can hard-crash if the instantiation is missing.
            int value = TypeManager.GetSystemTypeIndexNoThrow(il2cppType).Value;
            switch (SystemTypeIndexLookup.Classify(tableReady: true, value))
            {
                case SystemTypeIndexState.Resolved:
                    IndexByType[systemType] = value;
                    return value;
                case SystemTypeIndexState.NotRegistered:
                    // -1 from the native method: caching it would put a hook under an index no
                    // system ever has. Stay unresolved and say so once.
                    if (Reported.Add(systemType))
                        Plugin.Logger?.LogWarning(SystemTypeIndexLookup.NotRegisteredMessage(systemType.FullName ?? systemType.Name));
                    return 0;
                default:
                    return 0;
            }
        }
        catch (Exception e)
        {
            if (Reported.Add(systemType))
                Plugin.Logger?.LogError(SystemTypeIndexLookup.ThrewMessage(systemType.FullName ?? systemType.Name, e));
            return 0;
        }
    }

    /// <summary>
    /// True once <c>TypeManager.s_ManagedSystemTypeToIndex</c> is non-null, which is the exact
    /// pointer <c>GetSystemTypeIndexNoThrow</c> dereferences. Read as a raw static field so no
    /// <c>Dictionary&lt;Type, SystemTypeIndex&gt;</c> interop wrapper (a generic over a game value
    /// type) is ever constructed. If the field cannot be resolved on this build, fall through to
    /// the call and let its own failure be reported.
    /// </summary>
    private static bool SystemTableReady()
    {
        if (_systemTableReady) return true;
        try
        {
            IntPtr field = AccessResolver.FieldInfo(typeof(TypeManager), "Unity.Entities.TypeManager", "s_ManagedSystemTypeToIndex");
            IntPtr table = IntPtr.Zero;
            IL2CPP.il2cpp_field_static_get_value(field, &table);
            _systemTableReady = table != IntPtr.Zero;
        }
        catch (AccessException)
        {
            _systemTableReady = true;
        }
        return _systemTableReady;
    }

    private static void ResolvePending()
    {
        lock (Gate)
        {
            if (Unresolved.Count == 0) return;
            bool changed = false;
            for (int i = Unresolved.Count - 1; i >= 0; i--)
            {
                var entry = Unresolved[i];
                int index = ResolveIndex(entry.SystemType);
                if (index == 0) continue;
                Unresolved.RemoveAt(i);
                Place(entry, index);
                SortBucketPhase(index, entry.Before, entry.SystemName);
                changed = true;
            }
            if (changed) PublishSnapshot_NoLock();
        }
    }

    /// <summary>
    /// Copy-on-write publish of the hooked-index set and per-system handler arrays.
    /// Callers must hold <see cref="Gate"/>. Readers use <c>Volatile.Read</c> only.
    /// </summary>
    private static void PublishSnapshot_NoLock()
    {
        int unresolved = Unresolved.Count;
        if (ByIndex.Count == 0)
        {
            var empty = unresolved == 0
                ? Snapshot.Empty
                : new Snapshot(HookedIndexSet.Empty, NoHandlers, unresolved);
            Volatile.Write(ref _snapshot, empty);
            return;
        }

        var keys = new int[ByIndex.Count];
        var handlers = new Dictionary<int, HandlerPair>(ByIndex.Count);
        int i = 0;
        foreach (var kv in ByIndex)
        {
            keys[i++] = kv.Key;
            var bucket = kv.Value;
            handlers[kv.Key] = new HandlerPair(
                bucket.Before.Count == 0 ? Array.Empty<Entry>() : bucket.Before.ToArray(),
                bucket.After.Count == 0 ? Array.Empty<Entry>() : bucket.After.ToArray());
        }

        Volatile.Write(ref _snapshot, new Snapshot(HookedIndexSet.Create(keys), handlers, unresolved));
    }

    // ---- dispatch -----------------------------------------------------------------------------

    /// <summary>
    /// Job FHOOK3: <c>__state</c> is the <see cref="HookCallStack{T}"/> call token this prefix
    /// pushed, not a "did I push" bit. The bit is not lost — zero is never minted — and the token
    /// is what lets <see cref="PostfixManaged"/> release <em>this</em> call's frame instead of
    /// whatever is on top of the stack. On the Bootstrap path it travels out through the widened
    /// <c>ManagedUpdatePrefix</c> slot delegate; on the <c>HarmonyFallback</c> direct-patch path
    /// this method <b>is</b> the Harmony prefix and <c>__state</c> is Harmony's own local, typed
    /// per patch declaring type (<c>harmony-patches.md</c> §8.12), so all four methods here widen
    /// together and nothing outside this class is reached.
    /// </summary>
    internal static bool PrefixManaged(SystemBase __instance, ref long __state)
    {
        long perf = Dev.PerfPatchTimer.Start();
        __state = 0;
        var frame = default(CallFrame);
        bool runOriginal = true;
        try
        {
            var snap = Volatile.Read(ref _snapshot);
            if (snap.Indices.Count != 0 || snap.UnresolvedCount != 0)
            {
                if (__instance != null)
                {
                    SystemState* state = __instance.m_StatePtr;
                    if (state != null)
                        runOriginal = DispatchBefore(snap, state, __instance, ref frame);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: managed prefix threw: {e}");
        }
        if (frame.HasAfter)
            __state = Push(frame);
        Dev.PerfPatchTimer.End(Dev.PerfPatchTimer.SystemPrefixManaged, perf);
        return runOriginal;
    }

    internal static void PostfixManaged(long __state)
    {
        if (__state == 0) return;
        long perf = Dev.PerfPatchTimer.Start();
        var frame = Release(__state);
        if (frame.HasAfter)
        {
            try { DispatchAfter(in frame); }
            catch (Exception e) { Plugin.Logger?.LogError($"SystemHooks: managed postfix threw: {e}"); }
        }
        Dev.PerfPatchTimer.End(Dev.PerfPatchTimer.SystemPostfixManaged, perf);
    }

    /// <summary>The unmanaged twin, on the same token ABI and for the same reason.</summary>
    internal static bool PrefixUnmanaged(void* pSystemState, ref long __state)
    {
        long perf = Dev.PerfPatchTimer.Start();
        __state = 0;
        var frame = default(CallFrame);
        bool runOriginal = true;
        try
        {
            var snap = Volatile.Read(ref _snapshot);
            if (snap.Indices.Count != 0 && pSystemState != null)
                runOriginal = DispatchBefore(snap, (SystemState*)pSystemState, managed: null, ref frame);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: unmanaged prefix threw: {e}");
        }
        if (frame.HasAfter)
            __state = Push(frame);
        Dev.PerfPatchTimer.End(Dev.PerfPatchTimer.SystemPrefixUnmanaged, perf);
        return runOriginal;
    }

    internal static void PostfixUnmanaged(long __state)
    {
        if (__state == 0) return;
        long perf = Dev.PerfPatchTimer.Start();
        var frame = Release(__state);
        if (frame.HasAfter)
        {
            try { DispatchAfter(in frame); }
            catch (Exception e) { Plugin.Logger?.LogError($"SystemHooks: unmanaged postfix threw: {e}"); }
        }
        Dev.PerfPatchTimer.End(Dev.PerfPatchTimer.SystemPostfixUnmanaged, perf);
    }

    private static bool DispatchBefore(Snapshot snap, SystemState* state, SystemBase? managed, ref CallFrame frame)
    {
        int index = state->m_SystemTypeIndex.Value;
        Entry[]? before = null;
        Entry[]? after = null;

        if (index != 0 && snap.Indices.Contains(index))
        {
            if (snap.Handlers.TryGetValue(index, out var pair))
            {
                before = pair.Before.Length == 0 ? null : pair.Before;
                after = pair.After.Length == 0 ? null : pair.After;
            }
        }
        else if (index != 0 && managed != null && snap.UnresolvedCount > 0)
        {
            // TypeManager was not ready at register; adopt on first managed update of this type.
            // Unhooked systems with no pending registrations never take this lock.
            Type knownType = managed.GetType();
            lock (Gate)
            {
                if (AdoptUnresolved(knownType, index) != null)
                    PublishSnapshot_NoLock();
            }
            var adopted = Volatile.Read(ref _snapshot);
            if (index == 0 || !adopted.Handlers.TryGetValue(index, out var pair))
                return true;
            before = pair.Before.Length == 0 ? null : pair.Before;
            after = pair.After.Length == 0 ? null : pair.After;
        }
        else
        {
            return true;
        }

        if (before == null && after == null) return true;

        bool needWouldRun = NeedsWouldRun(before, after);
        bool wouldRun = !needWouldRun || TryWouldRun(state);
        World? world = null;
        bool skip = false;

        if (before != null)
        {
            foreach (var entry in before)
            {
                if (entry.Removed) continue;
                if (entry.OnlyWhenSystemRuns && !wouldRun) continue;
                if (!IsDue(entry)) continue;
                world ??= TryGetWorld(state);
                if (world == null) break;
                var ctx = new SystemHookContext(world, entry.SystemType, entry.SystemName, allowSkip: true);
                Invoke(entry, ctx);
                if (ctx.Skip) skip = true;
            }
        }

        if (after != null)
        {
            frame.HasAfter = true;
            frame.Skipped = skip;
            frame.WouldRun = wouldRun;
            frame.World = world ?? TryGetWorld(state);
            frame.After = after;
        }

        return !skip;
    }

    private static void DispatchAfter(in CallFrame frame)
    {
        if (frame.After == null || frame.World == null) return;
        foreach (var entry in frame.After)
        {
            if (entry.Removed) continue;
            if (entry.OnlyWhenSystemRuns && (frame.Skipped || !frame.WouldRun)) continue;
            if (!IsDue(entry)) continue;
            var ctx = new SystemHookContext(frame.World, entry.SystemType, entry.SystemName, allowSkip: false);
            Invoke(entry, ctx);
        }
    }

    private static Bucket? AdoptUnresolved(Type managedType, int index)
    {
        Bucket? bucket = null;
        string? name = null;
        for (int i = Unresolved.Count - 1; i >= 0; i--)
        {
            var entry = Unresolved[i];
            if (entry.SystemType != managedType) continue;
            Unresolved.RemoveAt(i);
            Place(entry, index);
            name = entry.SystemName;
            bucket ??= ByIndex[index];
        }
        if (bucket != null && name != null)
        {
            SortBucketPhase(index, beforePhase: true, name);
            SortBucketPhase(index, beforePhase: false, name);
        }
        return bucket;
    }

    /// <summary>
    /// The single funnel every plugin system hook goes through, and the only place that still
    /// knows which plugin id owns the callback — which is why the Dev <c>perf</c> op times it here
    /// (<see cref="Dev.PerfProbe"/>). Every call is also timed for the hitch summary
    /// (<see cref="Progenitor.Ui.UiFrameCost.NoteOutside"/>): hooks run inside the game's systems, outside the
    /// frame dispatch, and were otherwise invisible to it. Two timestamps per hooked call.
    /// </summary>
    private static void Invoke(Entry entry, SystemHookContext ctx)
    {
        var sink = Dev.PerfProbe.Sink;
        if (sink != null)
        {
            try { sink.BeginCallback(); }
            catch { /* a profiler must never break a hook */ }
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            entry.Hook(ctx);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: {(entry.Before ? "Before" : "After")} hook for {entry.SystemName} threw: {e}");
        }
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        Progenitor.Ui.UiFrameCost.NoteOutside(entry.CostName, ticks);
        if (sink == null) return;
        try { sink.SystemHook(entry.PluginId, entry.SystemName, entry.Before, ticks); }
        catch { /* a profiler must never break a hook */ }
    }

    private static bool NeedsWouldRun(Entry[]? before, Entry[]? after)
    {
        if (before != null)
        {
            for (int i = 0; i < before.Length; i++)
                if (before[i].OnlyWhenSystemRuns) return true;
        }
        if (after != null)
        {
            for (int i = 0; i < after.Length; i++)
                if (after[i].OnlyWhenSystemRuns) return true;
        }
        return false;
    }

    private static bool IsDue(Entry entry)
    {
        int n = entry.EveryNFrames;
        if (n <= 1) return true;
        uint t = entry.Elapsed++;
        return t % (uint)n == 0;
    }

    private static bool TryWouldRun(SystemState* state)
    {
        try
        {
            // Same two checks SystemBase.Update / UnmanagedUpdate apply after our prefix returns.
            return state->Enabled && state->ShouldRunSystem();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: Enabled/ShouldRunSystem threw: {e}");
            return true;
        }
    }

    private static World? TryGetWorld(SystemState* state)
    {
        try
        {
            var world = state->World;
            return world != null && world.IsCreated ? world : null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: SystemState.World threw: {e}");
            return null;
        }
    }

    private static long Push(CallFrame frame) => Frames.Push(in frame);

    private static CallFrame Release(long token) => Frames.Release(token);

    /// <summary>
    /// Job FLATCH1 (R44 finding 8). Harmony skips the postfix when the patched original throws,
    /// so a system that throws pushes a <see cref="CallFrame"/> that is never popped. The frame
    /// holds a <c>World</c> and the resolved <c>Entry[]</c> — plugin-owned delegates that
    /// <see cref="RemoveAll"/> can no longer reach, which is the pin that sweep exists to
    /// prevent. Released here on the boundary that makes it provable rather than probable: a
    /// system update cannot span two rendered frames, so anything still pushed from an earlier
    /// frame is abandoned. See <see cref="HookCallStack{T}"/>.
    ///
    /// <para><b>The mispairing this used to leave behind is closed (job FHOOK3), and the sweep is
    /// still not redundant.</b> If a caller <em>catches</em> the system's exception and returns
    /// normally — a system group's per-system try/catch — its own postfix used to pop positionally
    /// and take the inner system's stranded frame, dispatching the wrong After handlers and
    /// silently skipping its own. FHOOK2 fixed Bootstrap's half with a token in Harmony's
    /// <c>__state</c> and narrowed this to "the inner and the outer system both carry After
    /// handlers"; FHOOK3 widened the four update slot delegates from <c>bool hasAfter</c> to
    /// <c>long callToken</c> so the same identity reaches <see cref="PostfixManaged"/>, and
    /// <c>HookCallStack.Release</c> now takes this call's frame and drops the strand above it in
    /// the same pass.</para>
    ///
    /// <para><b>Why that narrow-looking residue was worth a cross-ALC ABI change.</b> The outer,
    /// catching system only needed After handlers, and this framework registers exactly that:
    /// <c>VirtualSystems.EnsureRelay</c> adds <c>After(typeof(SimulationSystemGroup))</c> the first
    /// time a plugin calls <c>ctx.Systems.Add</c> on the default anchor. That group derives from
    /// <c>SystemBase</c>, so its <c>Update</c> is patched, and it catches its children. So the
    /// residue's precondition was met by Progenitor's own flagship per-tick seam, and the visible
    /// symptom was every default-anchor virtual system silently not running — permanently, if the
    /// inner system threw every frame. The widening is a Bootstrap↔Core contract change and
    /// carries a protocol bump (<c>BootstrapLink.ProtocolVersion</c> 4), which is what turns a
    /// half-deployed pair into a refused load with both numbers logged instead of
    /// <c>Delegate.CreateDelegate</c> throwing in <c>Bind</c> and taking the site Inert.</para>
    ///
    /// <para><b>What still reaches the sweep.</b> A strand whose caller never returns through a
    /// patched <c>Update</c> at all — the outermost patched update on a chain throws and the catch
    /// sits in Unity's player loop, an unpatched group, or (routinely, on the unmanaged stack) the
    /// unmanaged caller of <c>UnmanagedUpdate</c>. No token covers that, because no enclosing
    /// frame is ever released; it would sit at the bottom of the stack for the life of the
    /// generation. Token for pairing, sweep for liveness. The <c>HarmonyFallback</c> direct-patch
    /// path is now covered by the token too: there these four methods <em>are</em> the Harmony
    /// prefix/postfix pair and <c>__state</c> is Harmony's own local.</para>
    ///
    /// <para><b>The alternative that would remove the strand outright, and why it is not taken</b>
    /// (job FDOC5, R45 finding 34). A Harmony <b>finalizer</b> runs on the throwing path too,
    /// which is the property whose absence creates the strand, and Bootstrap already ships one
    /// (<c>StunStartHook.Finalizer</c>) so it is not a mechanism to invent. The counterweight:
    /// these prefixes run for every system every frame, and a finalizer makes Harmony wrap the
    /// original in a try/catch on the hottest patched methods in the process. That is a reason,
    /// <b>not a measurement</b> — nobody has timed it on this build, and doing so needs a running
    /// client or server. Full argument, and the same caveat about the sweep's clock freezing on
    /// the degraded tick path, in Bootstrap's <c>AfterCallStack</c> remarks.</para>
    /// </summary>
    private static void ReleaseAbandonedFrames()
    {
        int dropped = Frames.NextFrame();
        if (dropped <= 0) return;
        if (Interlocked.Exchange(ref _abandonedLogged, 1) != 0) return;
        // Job FDOC5 (R45 finding 32): the wording lives in HookStrandReport, beside the stack it
        // describes and where a test can read it, exactly as Bootstrap's AfterCallStrand does.
        Plugin.Logger?.LogWarning(HookStrandReport.Report(dropped));
    }

    // ---- world waiters ------------------------------------------------------------------------

    private static void EnsureFrameHook()
    {
        lock (Gate)
        {
            if (_frameHooked) return;
            Plugin.Frame += OnFrame;
            _frameHooked = true;
        }
    }

    private static void OnFrame()
    {
        try
        {
            ReleaseAbandonedFrames();
            ResolvePending();
            DeliverWaiters();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: frame handler threw: {e}");
        }
    }

    private static void DeliverWaiters()
    {
        WorldWaiter[] snapshot;
        lock (Gate)
        {
            if (Waiters.Count == 0) return;
            snapshot = Waiters.ToArray();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            var waiter = snapshot[i];
            if (waiter.Fired) continue;
            World? world;
            try { world = Worlds.Find(waiter.WorldName); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"SystemHooks: Worlds.Find('{waiter.WorldName}') threw: {e}");
                continue;
            }
            if (world == null) continue;
            if (!waiter.TryClaim()) continue;
            try { waiter.Callback(world); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"SystemHooks: WhenWorldExists('{waiter.WorldName}') callback threw: {e}");
            }
        }
    }

    // The four cross-ALC update slot delegates. These declarations must stay CHARACTER-IDENTICAL
    // to the ones in Progenitor.Bootstrap.HarmonySlots: HarmonySlots.Bind joins them with
    // Delegate.CreateDelegate, which throws on any skew and takes the site Inert (FLATCH1 measured
    // that). A test pins the two texts against each other. Job FHOOK3 widened them from
    // `bool hasAfter` to `long callToken` so PostfixManaged can release the frame THIS call pushed
    // rather than whatever is on top of the stack; zero still means "the prefix pushed nothing",
    // so the old bit is not lost. A widening here is a Bootstrap<->Core contract change and must
    // bump BootstrapLink.ProtocolVersion / CoreLayout.ProtocolVersion in the same change.
    internal delegate bool ManagedUpdatePrefix(SystemBase instance, ref long callToken);
    internal delegate void ManagedUpdatePostfix(long callToken);
    internal delegate bool UnmanagedUpdatePrefix(IntPtr pSystemState, ref long callToken);
    internal delegate void UnmanagedUpdatePostfix(long callToken);

    internal static bool SlotPrefixManaged(SystemBase instance, ref long callToken) =>
        PrefixManaged(instance, ref callToken);

    internal static void SlotPostfixManaged(long callToken) => PostfixManaged(callToken);

    internal static bool SlotPrefixUnmanaged(IntPtr pSystemState, ref long callToken) =>
        PrefixUnmanaged((void*)pSystemState, ref callToken);

    internal static void SlotPostfixUnmanaged(long callToken) => PostfixUnmanaged(callToken);

    private static bool TryFillManagedSlots()
    {
        if (!HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.SystemBaseUpdate)) return false;
        return HarmonySlotHandoff.TryFillAll(
            new[] { HarmonySlotHandoff.SystemBaseUpdatePrefix, HarmonySlotHandoff.SystemBaseUpdatePostfix },
            new Delegate[]
            {
                new ManagedUpdatePrefix(SlotPrefixManaged),
                new ManagedUpdatePostfix(SlotPostfixManaged),
            });
    }

    private static bool TryFillUnmanagedSlots()
    {
        if (!HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.UnmanagedUpdate)) return false;
        return HarmonySlotHandoff.TryFillAll(
            new[] { HarmonySlotHandoff.UnmanagedUpdatePrefix, HarmonySlotHandoff.UnmanagedUpdatePostfix },
            new Delegate[]
            {
                new UnmanagedUpdatePrefix(SlotPrefixUnmanaged),
                new UnmanagedUpdatePostfix(SlotPostfixUnmanaged),
            });
    }

    // ---- nested types -------------------------------------------------------------------------

    private sealed class Entry
    {
        public Type SystemType = typeof(object);
        public string SystemName = "";
        public Action<SystemHookContext> Hook = _ => { };
        public bool Before;
        public bool OnlyWhenSystemRuns = true;
        public int EveryNFrames = 1;
        public uint Elapsed;
        public int TypeIndex;
        public bool Removed;
        public int Priority;
        public string PluginId = "";
        private string? _costName;
        /// <summary>The hitch summary's name for this hook: <c>hook:owner:System.before</c>.</summary>
        public string CostName => _costName ??= "hook:" + (PluginId.Length == 0 ? "progenitor" : PluginId) + ":"
            + SystemName + (Before ? ".before" : ".after");
        public string[] BeforeIds = Array.Empty<string>();
        public string[] AfterIds = Array.Empty<string>();
        public int Index;
        public int LoadOrder;

        public OrderSpec ToSpec() => new(Index, PluginId, Priority, BeforeIds, AfterIds, LoadOrder);
    }

    private sealed class Bucket
    {
        public readonly List<Entry> Before = new();
        public readonly List<Entry> After = new();
    }

    private readonly struct HandlerPair
    {
        public readonly Entry[] Before;
        public readonly Entry[] After;

        public HandlerPair(Entry[] before, Entry[] after)
        {
            Before = before;
            After = after;
        }
    }

    /// <summary>
    /// Immutable view of hooked type indices and their Before/After arrays, plus how many
    /// registrations are still waiting on TypeManager. Published with Volatile.Write.
    /// </summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(HookedIndexSet.Empty, NoHandlers, unresolvedCount: 0);

        public readonly HookedIndexSet Indices;
        public readonly Dictionary<int, HandlerPair> Handlers;
        public readonly int UnresolvedCount;

        public Snapshot(HookedIndexSet indices, Dictionary<int, HandlerPair> handlers, int unresolvedCount)
        {
            Indices = indices;
            Handlers = handlers;
            UnresolvedCount = unresolvedCount;
        }
    }

    private struct CallFrame
    {
        public bool HasAfter;
        public bool Skipped;
        public bool WouldRun;
        public World? World;
        public Entry[]? After;
    }

    private sealed class Registration : IDisposable
    {
        private Entry? _entry;
        public Registration(Entry entry) => _entry = entry;
        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry != null) Remove(entry);
        }
    }

    private sealed class WorldWaiter : IDisposable
    {
        public readonly string WorldName;
        public readonly Action<World> Callback;
        public bool Fired;
        private int _claimed;

        public WorldWaiter(string worldName, Action<World> callback)
        {
            WorldName = worldName;
            Callback = callback;
        }

        public bool TryClaim()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) return false;
            Fired = true;
            lock (Gate) Waiters.Remove(this);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) return;
            Fired = true;
            lock (Gate) Waiters.Remove(this);
        }
    }
}
