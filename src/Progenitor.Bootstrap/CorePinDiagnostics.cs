using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx.Logging;
using HarmonyLib;

namespace Progenitor.Bootstrap;

/// <summary>
/// Dump the process-lifetime caches that pin a collectible Core ALC. Harmony's IL2CPP
/// backend (<c>Il2CppDetourMethodPatcher</c>) applies a native detour and stores the
/// trampoline in a static <c>DelegateCache</c> that never forgets — Unpatch disposes the
/// JMP but leaves the trampoline. Same class of pin as <c>DelegateSupport.ConvertDelegate</c>.
/// Those two caches are <em>append-only</em> on a Core swap: removing a row drops the GC
/// root while Unity may still call <c>method_ptr</c> (client-hot-swap-crash.md). Pin-diag
/// them; never delete.
/// </summary>
internal static class CorePinDiagnostics
{
    public const string CoreHarmonyId = "progenitor";
    public const string BootstrapHarmonyId = "progenitor.bootstrap";

    public static void LogAll(Assembly? coreAssembly, string? alcName, Action<string>? log)
    {
        if (log == null) return;
        try { LogHarmony(coreAssembly, log); }
        catch (Exception e) { log("pin-diag Harmony: " + e.Message); }
        try { LogClassInjector(coreAssembly, log); }
        catch (Exception e) { log("pin-diag ClassInjector: " + e.Message); }
        try { LogDelegateSupport(coreAssembly, log); }
        catch (Exception e) { log("pin-diag DelegateSupport: " + e.Message); }
        try { LogHarmonyIl2CppCache(coreAssembly, log); }
        catch (Exception e) { log("pin-diag HarmonyIL2CPP: " + e.Message); }
        try { LogNativePins(log); }
        catch (Exception e) { log("pin-diag NativePinRegistry: " + e.Message); }
        try { LogNativePinsByGeneration(log); }
        catch (Exception e) { log("pin-diag native-pin-gen: " + e.Message); }
        try { LogLifetimeSlots(log); }
        catch (Exception e) { log("pin-diag lifetime slots: " + e.Message); }
        try { log(UiSlots.DescribePins() + " " + Progenitor.Ui.UiInputContext.DescribePins()); }
        catch (Exception e) { log("pin-diag UiSlots: " + e.Message); }
        // Job FLATCH3: the mask slot is the one field in UiMaskDetour that references collectible
        // Core, so mask=True while a swap is collecting is a pin, and mask=True with owns=False is
        // impossible. Its own line rather than appended to the UiSlots one, because the question
        // it answers on a failed collect is different.
        try { log(UiMaskDetour.DescribePins()); }
        catch (Exception e) { log("pin-diag UiMaskDetour: " + e.Message); }
        try { log(IconCache.DescribePins()); }
        catch (Exception e) { log("pin-diag IconCache: " + e.Message); }
        try { log(SaveSafety.DescribePins()); }
        catch (Exception e) { log("pin-diag SaveSafety: " + e.Message); }
        try { LogMainThread(alcName, coreAssembly, log); }
        catch (Exception e) { log("pin-diag MainThread: " + e.Message); }
        try { CoreReloadStackCheck.Log(coreAssembly, alcName, log); }
        catch (Exception e) { log("pin-diag stack: " + e.Message); }
        try { LogCollectibleAlcs(log); }
        catch (Exception e) { log("pin-diag alcs: " + e.Message); }
    }

    /// <summary>
    /// Every live collectible ALC in the process. Grep <c>pin-diag alcs=</c>.
    /// Call at plugin-collect and Core-collect time so a leftover
    /// <c>progenitor-reload:*</c> that still pins Core is visible by name.
    /// </summary>
    public static void LogCollectibleAlcs(Action<string> log)
    {
        var names = new List<string>();
        try
        {
            foreach (var alc in AssemblyLoadContext.All)
            {
                if (alc == null || !alc.IsCollectible) continue;
                names.Add(alc.Name ?? "");
            }
        }
        catch (Exception e)
        {
            log("pin-diag alcs: " + e.Message);
            log(CoreReloadPolicy.FormatCollectibleAlcs(
                CoreReloadPolicy.ClosePluginAlcListing(names, enumerationFailed: true)));
            return;
        }
        log(CoreReloadPolicy.FormatCollectibleAlcs(names));
    }

    /// <summary>
    /// After Core UnpatchID, diagnose IL2CPP Harmony / DelegateSupport rows whose
    /// <c>MethodInfo</c> lives in <paramref name="coreAssembly"/> (never remove: those
    /// caches are append-only) and <c>ReleaseGeneration</c> NativePinRegistry entries.
    /// Skips pin-release when leftover Harmony JMPs remain. Never <c>ReleaseAll</c>s
    /// pins of another generation.
    /// </summary>
    public static int ReleaseCorePins(Assembly? coreAssembly, string? generation, int leftoverHarmony, Action<string>? log)
    {
        if (!CoreReloadPolicy.MayClearIl2CppCaches(leftoverHarmony))
        {
            log?.Invoke("pin-release skipped: leftover Harmony JMPs still live (will not mutate IL2CPP caches)");
            return 0;
        }
        int n = 0;
        try { n += ClearHarmonyIl2CppCache(coreAssembly, log); }
        catch (Exception e) { log?.Invoke("pin-release HarmonyIL2CPP: " + e.Message); }
        try { n += ClearDelegateSupport(coreAssembly, log); }
        catch (Exception e) { log?.Invoke("pin-release DelegateSupport: " + e.Message); }
        int pinDropped = 0;
        try
        {
            int before = NativePinRegistry.CountFor(generation);
            pinDropped = NativePinRegistry.ReleaseGeneration(generation, out var droppedNames, out var remainingNames);
            int after = NativePinRegistry.CountFor(generation);
            n += pinDropped;
            log?.Invoke(NativePinRegistry.FormatReleaseLine(generation, before, after, droppedNames, remainingNames));
        }
        catch (Exception e) { log?.Invoke("pin-release NativePinRegistry: " + e.Message); }
        log?.Invoke("pin-release dropped=" + n + " cache entries/handles gen=" + (generation ?? ""));
        return n;
    }

    /// <summary>Overload used by older call sites; counts leftovers first.</summary>
    public static int ReleaseCorePins(Assembly? coreAssembly, Action<string>? log)
    {
        int leftover = 0;
        try { leftover = CountCoreHarmonyLeftovers(coreAssembly); }
        catch { leftover = 0; }
        return ReleaseCorePins(coreAssembly, generation: null, leftover, log);
    }

    public static void LogHarmony(Assembly? coreAssembly, Action<string> log)
    {
        int coreOwned = 0;
        int leftoverAfterId = 0;
        var lines = new List<string>();
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            if (method == null) continue;
            var info = Harmony.GetPatchInfo(method);
            if (info == null) continue;
            Collect(info.Prefixes);
            Collect(info.Postfixes);
            Collect(info.Transpilers);
            Collect(info.Finalizers);

            void Collect(IEnumerable<Patch>? patches)
            {
                if (patches == null) return;
                foreach (var p in patches)
                {
                    if (p == null) continue;
                    string owner = p.owner ?? "";
                    bool coreId = IsCoreHarmonyId(owner);
                    bool coreMethod = coreAssembly != null && p.PatchMethod?.DeclaringType?.Assembly == coreAssembly;
                    if (!coreId && !coreMethod) continue;
                    coreOwned++;
                    string target = method.DeclaringType?.Name + "." + method.Name;
                    string patch = p.PatchMethod?.DeclaringType?.Name + "." + p.PatchMethod?.Name;
                    lines.Add("Harmony owner=" + owner + " target=" + target + " patch=" + patch);
                    if (coreId && owner != BootstrapHarmonyId)
                        leftoverAfterId++;
                }
            }
        }
        log("pin-diag Harmony core-owned=" + coreOwned + (lines.Count == 0 ? "" : ":"));
        for (int i = 0; i < lines.Count && i < 32; i++)
            log("  " + lines[i]);
        if (lines.Count > 32)
            log("  ... " + (lines.Count - 32) + " more");
        _ = leftoverAfterId;
    }

    public static int CountCoreHarmonyLeftovers(Assembly? coreAssembly)
    {
        int n = 0;
        try
        {
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                n += Count(info.Prefixes);
                n += Count(info.Postfixes);
                n += Count(info.Transpilers);
                n += Count(info.Finalizers);

                int Count(IEnumerable<Patch>? patches)
                {
                    if (patches == null) return 0;
                    int c = 0;
                    foreach (var p in patches)
                    {
                        if (p == null) continue;
                        if (IsCoreHarmonyId(p.owner)) c++;
                        else if (coreAssembly != null && p.PatchMethod?.DeclaringType?.Assembly == coreAssembly) c++;
                    }
                    return c;
                }
            }
        }
        catch
        {
            return n;
        }
        return n;
    }

    public static void UnpatchCoreHarmony(Action<string>? log)
    {
        try
        {
            Harmony.UnpatchID(CoreHarmonyId);
            log?.Invoke("Harmony.UnpatchID('" + CoreHarmonyId + "')");
        }
        catch (Exception e)
        {
            log?.Invoke("Harmony.UnpatchID('" + CoreHarmonyId + "'): " + e.Message);
        }
    }

    private static bool IsCoreHarmonyId(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        if (string.Equals(owner, BootstrapHarmonyId, StringComparison.Ordinal)) return false;
        return string.Equals(owner, CoreHarmonyId, StringComparison.Ordinal)
            || owner.StartsWith(CoreHarmonyId + ".", StringComparison.Ordinal);
    }

    public static void LogClassInjector(Assembly? coreAssembly, Action<string> log)
    {
        var type = Type.GetType("Il2CppInterop.Runtime.Injection.ClassInjector, Il2CppInterop.Runtime");
        if (type == null)
        {
            log("pin-diag ClassInjector: type not loaded");
            return;
        }
        var field = type.GetField("InjectedTypes", BindingFlags.NonPublic | BindingFlags.Static);
        var set = field?.GetValue(null) as IEnumerable;
        int n = 0;
        int coreHits = 0;
        int bootstrapHits = 0;
        var names = new List<string>();
        if (set != null)
        {
            foreach (var item in set)
            {
                n++;
                string name = item as string ?? item?.ToString() ?? "";
                names.Add(name);
                if (CoreReloadPolicy.IsBootstrapInjectedFullName(name))
                    bootstrapHits++;
                else if (CoreReloadPolicy.IsClassInjectorCorePin(name))
                    coreHits++;
            }
        }
        log("pin-diag ClassInjector injected=" + n
            + " progenitor-named=" + coreHits
            + " bootstrap-ticker=" + (bootstrapHits > 0));
        if (coreHits > 0)
            log(CoreReloadPolicy.FormatClassInjectorWillNotCollect(coreHits));
        for (int i = 0; i < names.Count; i++)
            log(CoreReloadPolicy.FormatInjectedTypeLine(names[i]));
        _ = coreAssembly;
    }

    public static void LogDelegateSupport(Assembly? coreAssembly, Action<string> log)
    {
        var type = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
        if (type == null)
        {
            log("pin-diag DelegateSupport: type not loaded");
            return;
        }
        int trampolines = CountDictionary(type, "NativeToManagedTrampolines", coreAssembly, out int coreTrampolines, out var samples);
        log("pin-diag DelegateSupport NativeToManagedTrampolines=" + trampolines + " core=" + coreTrampolines);
        for (int i = 0; i < samples.Count && i < 16; i++)
            log("  trampoline " + samples[i]);
    }

    public static void LogHarmonyIl2CppCache(Assembly? coreAssembly, Action<string> log)
    {
        if (!TryReadDelegateCache(out var list, out var error))
        {
            log(error ?? "pin-diag HarmonyIL2CPP DelegateCache: skip");
            return;
        }
        var entries = ReadDelegateCacheEntries(list);
        int core = 0;
        string? coreName = null;
        try { coreName = coreAssembly?.GetName().Name; }
        catch { coreName = null; }
        if (coreName != null)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].MethodAssemblyName, coreName, StringComparison.Ordinal))
                    core++;
            }
        }
        var lines = new List<string>();
        int collectible = DetourCachePolicy.DescribeCollectible(entries, lines);
        log(DetourCachePolicy.FormatDelegateCacheLog(list.Count, core, collectible));
        for (int i = 0; i < lines.Count; i++)
            log(lines[i]);
        if (collectible > lines.Count)
            log("  ... " + (collectible - lines.Count) + " more");
    }

    /// <summary>
    /// Methods in the IL2CPP detour cache that pin <paramref name="assembly"/> /
    /// <paramref name="alcName"/>. Empty when the cache is missing or this context
    /// is not in it.
    /// </summary>
    public static List<string> DetourCachePinsFor(Assembly? assembly, string? alcName)
    {
        if (!TryReadDelegateCache(out var list, out _))
            return new List<string>();
        var entries = ReadDelegateCacheEntries(list);
        string? assemblyName = null;
        try { assemblyName = assembly?.GetName().Name; }
        catch { assemblyName = null; }
        return DetourCachePolicy.MethodsPinningContext(entries, assemblyName, alcName);
    }

    private static bool _loggedCacheMissing;

    /// <summary>
    /// True when a static <c>List&lt;object&gt;</c> on the IL2CPP patcher was
    /// resolved. False means skip-wait is disabled (fail closed: wait the normal
    /// frames; treat plugins as possibly pinned).
    /// </summary>
    internal static bool DetourCacheAvailable { get; private set; } = true;

    internal static bool TryReadDelegateCache(out IList list, out string? error)
    {
        list = Array.Empty<object>();
        error = null;
        var type = Type.GetType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher, Il2CppInterop.HarmonySupport");
        if (type == null)
        {
            error = "pin-diag HarmonyIL2CPP: Il2CppDetourMethodPatcher not loaded";
            MarkCacheMissing(error);
            return false;
        }
        var field = DetourCachePolicy.FindStaticListObjectField(type);
        if (field == null)
        {
            error = DetourCachePolicy.FormatCacheMissing();
            MarkCacheMissing(error);
            return false;
        }
        DetourCacheAvailable = true;
        object? cache;
        try { cache = field.GetValue(null); }
        catch (Exception e)
        {
            error = "pin-diag HarmonyIL2CPP DelegateCache: get threw " + e.Message;
            return false;
        }
        if (cache is not IList live)
        {
            error = "pin-diag HarmonyIL2CPP DelegateCache: not IList runtimeType=" + (cache?.GetType().FullName ?? "null") + "; skip";
            return false;
        }
        list = live;
        return true;
    }

    private static void MarkCacheMissing(string? error)
    {
        DetourCacheAvailable = false;
        if (_loggedCacheMissing) return;
        _loggedCacheMissing = true;
        try { ProcessHost.Log?.LogWarning(error ?? DetourCachePolicy.FormatCacheMissing()); }
        catch { /* pin-diag */ }
    }

    /// <summary>Project a live cache onto game-free <see cref="DetourCachePolicy.Entry"/> rows.</summary>
    internal static List<DetourCachePolicy.Entry> ReadDelegateCacheEntries(IList? cache)
    {
        var entries = new List<DetourCachePolicy.Entry>();
        if (cache == null) return entries;
        foreach (var item in cache)
        {
            try
            {
                if (item is not Delegate d) continue;
                entries.Add(DescribeDelegate(d));
            }
            catch
            {
                // Il2CppInterop trampoline .Method can throw NotSupportedException.
            }
        }
        return entries;
    }

    internal static DetourCachePolicy.Entry DescribeDelegate(Delegate d)
    {
        var entry = new DetourCachePolicy.Entry();
        try
        {
            var method = d.Method;
            var declaring = method?.DeclaringType;
            entry.MethodName = declaring == null ? method?.Name : declaring.FullName + "." + method?.Name;
            var methodAsm = declaring?.Assembly;
            try { entry.MethodAssemblyName = methodAsm?.GetName().Name; }
            catch { /* */ }
            DescribeAssembly(methodAsm, out entry.MethodAlcName, out entry.MethodAlcCollectible);
        }
        catch
        {
            // trampoline .Method
        }
        try
        {
            var target = d.Target;
            if (target != null)
            {
                var targetType = target.GetType();
                entry.TargetTypeName = targetType.FullName;
                var targetAsm = targetType.Assembly;
                try { entry.TargetAssemblyName = targetAsm.GetName().Name; }
                catch { /* */ }
                DescribeAssembly(targetAsm, out entry.TargetAlcName, out entry.TargetAlcCollectible);
            }
        }
        catch
        {
            // target type
        }
        return entry;
    }

    private static void DescribeAssembly(Assembly? assembly, out string? alcName, out bool collectible)
    {
        alcName = null;
        collectible = false;
        if (assembly == null) return;
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(assembly);
            if (alc == null) return;
            alcName = alc.Name;
            collectible = alc.IsCollectible;
        }
        catch
        {
            // GetLoadContext can throw on an unloading assembly.
        }
    }

    /// <summary>
    /// Diagnose-only. Harmony IL2CPP <c>DelegateCache</c> is append-only; removing a
    /// trampoline Unity or a leftover JMP still holds is a native death. Returns 0
    /// dropped so pin-release counts only NativePinRegistry.
    /// </summary>
    public static int ClearHarmonyIl2CppCache(Assembly? coreAssembly, Action<string>? log = null)
    {
        int core = CountHarmonyIl2CppCoreRows(coreAssembly, log);
        log?.Invoke(CoreReloadPolicy.FormatHarmonyIl2CppAppendOnly(core));
        return 0;
    }

    /// <summary>
    /// Diagnose-only. <c>DelegateSupport.NativeToManagedTrampolines</c> is append-only
    /// (Il2CppInterop GetOrAdd, no Remove in upstream). A removed row drops the GC
    /// root while Unity may still call <c>method_ptr</c>. Returns 0 dropped.
    /// </summary>
    public static int ClearDelegateSupport(Assembly? coreAssembly, Action<string>? log = null)
    {
        int core = CountDelegateSupportCoreRows(coreAssembly, log);
        log?.Invoke(CoreReloadPolicy.FormatDelegateSupportAppendOnly(core));
        return 0;
    }

    private static int CountHarmonyIl2CppCoreRows(Assembly? coreAssembly, Action<string>? log)
    {
        if (coreAssembly == null) return 0;
        var type = Type.GetType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher, Il2CppInterop.HarmonySupport");
        var field = DetourCachePolicy.FindStaticListObjectField(type);
        object? cache;
        try { cache = field?.GetValue(null); }
        catch (Exception e)
        {
            log?.Invoke("pin-release HarmonyIL2CPP DelegateCache get: " + e.Message);
            return 0;
        }
        if (cache is not IList list)
        {
            log?.Invoke("pin-release HarmonyIL2CPP DelegateCache runtimeType=" + (cache?.GetType().FullName ?? "null") + " not IList; skip");
            return 0;
        }
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            try
            {
                if (list[i] is not Delegate d) continue;
                MethodInfo? method;
                try { method = d.Method; }
                catch { continue; }
                if (method?.DeclaringType?.Assembly != coreAssembly) continue;
                n++;
            }
            catch
            {
                // One bad entry must not abort the rest of the count.
            }
        }
        return n;
    }

    private static int CountDelegateSupportCoreRows(Assembly? coreAssembly, Action<string>? log)
    {
        if (coreAssembly == null) return 0;
        var type = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
        var field = type?.GetField("NativeToManagedTrampolines", BindingFlags.NonPublic | BindingFlags.Static);
        object? dict;
        try { dict = field?.GetValue(null); }
        catch (Exception e)
        {
            log?.Invoke("pin-release DelegateSupport get: " + e.Message);
            return 0;
        }
        if (dict == null)
        {
            log?.Invoke("pin-release DelegateSupport NativeToManagedTrampolines=null; skip");
            return 0;
        }
        var keysProp = dict.GetType().GetProperty("Keys");
        IEnumerable? keys;
        try { keys = keysProp?.GetValue(dict) as IEnumerable; }
        catch (Exception e)
        {
            log?.Invoke("pin-release DelegateSupport Keys: " + e.Message);
            return 0;
        }
        if (keys == null)
        {
            log?.Invoke("pin-release DelegateSupport Keys not enumerable runtimeType=" + dict.GetType().FullName + "; skip");
            return 0;
        }
        int n = 0;
        try
        {
            foreach (var key in keys)
            {
                try
                {
                    if (key is MethodInfo mi && mi.DeclaringType?.Assembly == coreAssembly)
                        n++;
                }
                catch
                {
                    // per-item
                }
            }
        }
        catch (Exception e)
        {
            log?.Invoke("pin-release DelegateSupport enumerate: " + e.Message);
        }
        return n;
    }

    public static void LogNativePins(Action<string> log)
    {
        var lines = NativePinRegistry.Describe();
        log("pin-diag NativePinRegistry count=" + lines.Count);
        for (int i = 0; i < lines.Count; i++)
            log(lines[i]);
    }

    /// <summary>
    /// NativePinRegistry grouped by Keep-time generation. A gen-2 pin still tagged
    /// as gen 1 is the PinGeneration-not-advanced miss (evidence 61).
    /// </summary>
    public static void LogNativePinsByGeneration(Action<string> log)
    {
        var groups = NativePinRegistry.CountByGeneration();
        if (groups.Count == 0)
        {
            log(CoreReloadPolicy.FormatNativePinGeneration("", 0));
            return;
        }
        for (int i = 0; i < groups.Count; i++)
            log(CoreReloadPolicy.FormatNativePinGeneration(groups[i].Key, groups[i].Value));
    }

    /// <summary>
    /// Walk Bootstrap statics and the live loader/driver/slot instances for a strong
    /// ref into <paramref name="coreAssembly"/> / <paramref name="alc"/>. Call after
    /// published Core refs and slots have been dropped, immediately before
    /// <c>ALC.Unload</c> (the walk needs the assembly identity; AppDomain / ALC
    /// listings of that identity are ignored). The plain scan also walks four cheap
    /// extra roots (AppDomain.ProcessExit, Logger.Listeners, Logger.Sources,
    /// DelegateSupport statics). <paramref name="wide"/> enumerates allowlisted
    /// non-collectible assemblies.
    /// </summary>
    public static void LogHolders(
        Assembly? coreAssembly,
        AssemblyLoadContext? alc,
        object?[] roots,
        Action<string> log,
        bool wide = false)
    {
        var extra = new List<object?>();
        CollectCheapRoots(extra);
        if (wide) CollectWideNamedRoots(extra);
        object?[] allRoots = ConcatRoots(roots, extra);
        if (wide)
        {
            CoreReloadHolderScan.WalkWide(
                assemblies: null,
                extraRoots: allRoots,
                unloadingAssembly: coreAssembly,
                unloadingAlc: alc,
                log: log);
        }
        else
        {
            var types = new Type[]
            {
                typeof(CoreLoader),
                typeof(ProcessHost),
                typeof(HarmonySlots),
                typeof(HarmonyPatches),
                typeof(ReceiveDetours),
                typeof(ReloaderHost),
                typeof(NativePinRegistry),
                typeof(WorkQueue),
                typeof(Plugin),
                typeof(SelfUpdate),
                typeof(CoreReloadFrameDriver),
                typeof(CoreReloadOrchestrator),
            };
            CoreReloadHolderScan.Walk(types, allRoots, coreAssembly, alc, log);
        }
        LogNativePinsByGeneration(log);
    }

    /// <summary>
    /// Four cheap extra roots for the plain scan, plus AppDomain.CurrentDomain so
    /// ProcessExit / AssemblyLoad / UnhandledException backing fields are visible.
    /// </summary>
    internal static void CollectCheapRoots(List<object?> into)
    {
        if (into == null) return;
        try { into.Add(ReadInstanceEvent(AppDomain.CurrentDomain, "ProcessExit")); } catch { /* */ }
        try { into.Add(Logger.Listeners); } catch { /* */ }
        try { into.Add(Logger.Sources); } catch { /* */ }
        try { AddTypeStatics(into, Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime")); }
        catch { /* */ }
    }

    /// <summary>
    /// Named tables the wide scan should hit even if assembly enumeration misses them:
    /// AppDomain event backing fields (not CurrentDomain itself — its loaded-assembly
    /// list is an expected listing until ALC.Unload, R16 finding 4), Il2CppObjectPool,
    /// ClassInjector, Harmony shared state.
    /// </summary>
    internal static void CollectWideNamedRoots(List<object?> into)
    {
        if (into == null) return;
        try { into.Add(ReadInstanceEvent(AppDomain.CurrentDomain, "AssemblyLoad")); } catch { /* */ }
        try { into.Add(ReadInstanceEvent(AppDomain.CurrentDomain, "UnhandledException")); } catch { /* */ }
        try { AddTypeStatics(into, Type.GetType("Il2CppInterop.Runtime.Runtime.Il2CppObjectPool, Il2CppInterop.Runtime")); }
        catch { /* */ }
        try { AddTypeStatics(into, Type.GetType("Il2CppInterop.Runtime.Injection.ClassInjector, Il2CppInterop.Runtime")); }
        catch { /* */ }
        try { AddTypeStatics(into, Type.GetType("HarmonyLib.HarmonySharedState, 0Harmony")); }
        catch { /* */ }
        try { AddTypeStatics(into, Type.GetType("HarmonyLib.PatchFunctions, 0Harmony")); }
        catch { /* */ }
        try { AddTypeStatics(into, Type.GetType("HarmonyLib.Internal.Patching.PatchFunctions, 0Harmony")); }
        catch { /* */ }
    }

    private static object?[] ConcatRoots(object?[]? roots, List<object?> extra)
    {
        int n = (roots == null ? 0 : roots.Length) + extra.Count;
        var all = new object?[n];
        int w = 0;
        if (roots != null)
        {
            for (int i = 0; i < roots.Length; i++)
                all[w++] = roots[i];
        }
        for (int i = 0; i < extra.Count; i++)
            all[w++] = extra[i];
        return all;
    }

    private static void AddTypeStatics(List<object?> into, Type? type)
    {
        if (type == null) return;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo[] fields;
        try { fields = type.GetFields(flags); }
        catch { return; }
        for (int i = 0; i < fields.Length; i++)
        {
            try { into.Add(fields[i].GetValue(null)); }
            catch { /* per-field */ }
        }
    }

    private static object? ReadInstanceEvent(object target, string eventName)
    {
        if (target == null || string.IsNullOrEmpty(eventName)) return null;
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string[] names =
        {
            eventName,
            "_" + eventName,
            eventName.Length > 0 ? char.ToLowerInvariant(eventName[0]) + eventName.Substring(1) : eventName,
        };
        for (int i = 0; i < names.Length; i++)
        {
            try
            {
                var field = type.GetField(names[i], flags);
                if (field == null) continue;
                return field.GetValue(target);
            }
            catch
            {
                // next name
            }
        }
        try
        {
            var ev = type.GetEvent(eventName, flags);
            var fi = ev?.DeclaringType?.GetField(eventName, flags);
            return fi?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    public static void LogLifetimeSlots(Action<string> log)
    {
        log("pin-diag LifetimeQuitting=" + (ProcessHost.LifetimeQuitting != null)
            + " LifetimeFrame=" + (ProcessHost.LifetimeFrame != null)
            + " CoreTick=" + (ProcessHost.CoreTick != null)
            + " LateFrame=" + (ProcessHost.LateFrame != null)
            + " SweepPlugin=" + (ProcessHost.SweepPlugin != null)
            + " " + UiSlots.DescribePins() + " " + Progenitor.Ui.UiInputContext.DescribePins());
    }

    public static void LogMainThread(string? alcName, Assembly? coreAssembly, Action<string> log)
    {
        log(WorkQueue.DescribeOwned(alcName, coreAssembly?.FullName));
    }

    private static int CountDictionary(Type type, string fieldName, Assembly? coreAssembly, out int coreHits, out List<string> samples)
    {
        coreHits = 0;
        samples = new List<string>();
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        var dict = field?.GetValue(null);
        if (dict == null) return 0;
        int n = 0;
        if (dict is IEnumerable en)
        {
            foreach (var item in en)
            {
                n++;
                if (item == null) continue;
                var t = item.GetType();
                var key = t.GetProperty("Key")?.GetValue(item) ?? t.GetField("Key")?.GetValue(item);
                var mi = key as MethodInfo;
                if (mi == null && key is Delegate d)
                    mi = d.Method;
                if (mi == null || coreAssembly == null) continue;
                if (mi.DeclaringType?.Assembly != coreAssembly) continue;
                coreHits++;
                samples.Add((mi.DeclaringType?.FullName ?? "?") + "." + mi.Name);
            }
        }
        return n;
    }

    public static string FormatLeftoverLine(int leftover) =>
        leftover == 0
            ? "Harmony leftovers after UnpatchID(" + CoreHarmonyId + ")=0"
            : "Harmony leftovers after UnpatchID(" + CoreHarmonyId + ")=" + leftover;
}
