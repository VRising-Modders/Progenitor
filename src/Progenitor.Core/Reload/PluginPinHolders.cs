using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;

namespace Progenitor.Reload;

/// <summary>
/// "Who in Core still holds this unloaded plugin" (grep <c>pin-diag plugin-holders</c>).
///
/// <para>Every plugin unload records its collectible load context here, weakly. When a Core swap
/// diagnoses pins, or a refused swap restores plugins, each recorded context that is still alive is
/// a plugin ALC that should have been collected. For those only, a bounded reflective walk of the
/// static fields of Core and Bootstrap names every path that reaches an object, delegate, type or
/// assembly from that context, e.g. <c>Ui.VanillaPanelTabs.Live[0]._owner -&gt; Example.Mod.Plugin</c>.
/// Nothing runs while every recorded context has been collected, so a healthy reload pays one
/// weak-reference check per unloaded plugin.</para>
///
/// <para>A holder no field walk can see is a runtime cross-reference: a generic the plugin defines,
/// instantiated with Core types, lives in Core's context (<see cref="CrossContextGenerics"/>). Each
/// survivor's offending members are logged as <c>pin-diag plugin-holder runtime-generic</c>, and the
/// slot in Core's LoaderAllocator handle table that holds it when that table is readable.</para>
///
/// <para>Game-free: the walk takes its roots and its target test as arguments, so it is unit-tested
/// without a game or a second load context.</para>
/// </summary>
internal static class PluginPinHolders
{
    /// <summary>Field-graph depth. A Dictionary value is static, dictionary, entries, entry, value: five.</summary>
    internal const int MaxDepth = 9;

    /// <summary>Holder lines per scan; further hits are counted, not printed.</summary>
    internal const int MaxHits = 24;

    /// <summary>The walk stops when this budget is spent and says so.</summary>
    internal const int BudgetMilliseconds = 250;

    /// <summary>Array elements read per array; the rest are counted as truncated.</summary>
    internal const int MaxArrayElements = 4096;

    private sealed class Retired
    {
        internal readonly WeakReference<AssemblyLoadContext> Context;
        internal readonly string Name;
        internal readonly string Plugin;
        internal readonly long RetiredAt;
        /// <summary>Generics the plugin defines that live in Core's context (<see cref="CrossContextGenerics"/>).</summary>
        internal readonly string[] RuntimeGenerics;
        /// <summary>The plugin's managed LoaderAllocator, weakly, to find it in Core's handle table.</summary>
        internal readonly WeakReference<object>? Allocator;

        internal Retired(AssemblyLoadContext context, string name, string plugin, long retiredAt,
            string[]? runtimeGenerics = null, object? allocator = null)
        {
            Context = new WeakReference<AssemblyLoadContext>(context);
            Name = name;
            Plugin = plugin;
            RetiredAt = retiredAt;
            RuntimeGenerics = runtimeGenerics ?? Array.Empty<string>();
            Allocator = allocator != null ? new WeakReference<object>(allocator) : null;
        }
    }

    private static readonly object Gate = new();
    private static readonly List<Retired> RetiredContexts = new();

    /// <summary>
    /// Record that <paramref name="pluginType"/>'s collectible context is unloading. Never throws.
    /// A context that is not collectible (a chainloader plugin) is not recorded.
    /// </summary>
    internal static void NoteUnloaded(Type? pluginType, string? pluginName)
    {
        try
        {
            if (pluginType == null) return;
            var alc = AssemblyLoadContext.GetLoadContext(pluginType.Assembly);
            if (alc == null || !alc.IsCollectible) return;
            // The admission scan's result, or a scan now: the assembly is still loaded. Strings only.
            // Computed before the lock (a scan takes milliseconds); the check and the add share one lock.
            var generics = RuntimeGenericsOf(pluginType.Assembly);
            var allocator = CrossContextGenerics.LoaderAllocatorOf(pluginType);
            lock (Gate)
            {
                Prune();
                for (int i = 0; i < RetiredContexts.Count; i++)
                    if (RetiredContexts[i].Context.TryGetTarget(out var known) && ReferenceEquals(known, alc)) return;
                RetiredContexts.Add(new Retired(alc, alc.Name ?? "?", pluginName ?? pluginType.FullName ?? "?", Stopwatch.GetTimestamp(),
                    generics, allocator));
            }
        }
        catch
        {
            // Diagnostics must never break an unload.
        }
    }

    private static string[] RuntimeGenericsOf(Assembly assembly)
    {
        string name;
        try { name = assembly.GetName().Name ?? ""; }
        catch { name = ""; }
        var findings = CrossContextGenerics.Recall(name) ?? CrossContextGenerics.ScanLoaded(assembly, out _);
        if (findings == null || findings.Count == 0) return Array.Empty<string>();
        // Possible (generic dispatch) findings are not printed as holders; the m_slots line covers them.
        var members = new List<string>(findings.Count);
        foreach (var f in findings) if (!f.Possible) members.Add(f.Member);
        return members.ToArray();
    }

    /// <summary>Recorded contexts still alive (test and status use).</summary>
    internal static int AliveCount
    {
        get { lock (Gate) { Prune(); return RetiredContexts.Count; } }
    }

    /// <summary>Forget every recorded context (tests). A new Core generation starts with an empty table of its own.</summary>
    internal static void Clear()
    {
        lock (Gate) RetiredContexts.Clear();
    }

    /// <summary>
    /// Scan for holders of every recorded plugin context that is still alive and was unloaded at
    /// least <paramref name="minAgeSeconds"/> ago. Zero after a refused swap (every survivor already
    /// outlived the collect wait); a few seconds at the pre-wait diagnosis, so contexts unloaded by
    /// this very swap, which have simply not been collected yet, are not walked.
    /// </summary>
    internal static void Diagnose(Action<string> log, double minAgeSeconds)
    {
        if (log == null) return;
        List<(AssemblyLoadContext alc, string name, string plugin, Retired record)> targets;
        try { targets = AliveTargets(minAgeSeconds); }
        catch (Exception e) { log("pin-diag plugin-holders: " + e.Message); return; }
        if (targets.Count == 0) return;

        // The budget covers the type listing too, not only the walk.
        var clock = Stopwatch.StartNew();
        var alcs = new HashSet<AssemblyLoadContext>(ReferenceEqualityComparer.Instance);
        var names = new List<string>();
        foreach (var t in targets)
        {
            alcs.Add(t.alc);
            names.Add(t.plugin + " (" + t.name + ")");
        }
        log("pin-diag plugin-holders alive=" + targets.Count.ToString(CultureInfo.InvariantCulture) + " [" + string.Join(", ", names) + "]");

        var roots = new List<Type>();
        try { AddTypes(roots, typeof(PluginPinHolders).Assembly); }
        catch (Exception e) { log("pin-diag plugin-holders core types: " + e.Message); }
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? n;
                try { n = asm.GetName().Name; }
                catch { continue; }
                if (n == "Progenitor.Bootstrap") AddTypes(roots, asm);
            }
        }
        catch (Exception e) { log("pin-diag plugin-holders bootstrap types: " + e.Message); }

        var inAny = IsInAny(alcs);
        Walk(roots, t => inAny(t.Assembly), inAny, log, BudgetMilliseconds, alcs, clock);
        var records = new List<Retired>(targets.Count);
        foreach (var t in targets) records.Add(t.record);
        ReportRuntimeReferences(records, CrossContextGenerics.LoaderAllocatorOf(typeof(PluginPinHolders)), log);
    }

    /// <summary>
    /// Holders no static walk can see: a generic the plugin defines, instantiated with Core types, is
    /// created in Core's context, and the runtime records a permanent reference from Core's
    /// LoaderAllocator to the plugin's (grep <c>runtime-generic</c>). Logs the members the metadata
    /// scan named and, when the runtime's handle table is readable, the slot that holds the plugin.
    /// Returns the number of lines. Never throws.
    /// </summary>
    private static int ReportRuntimeReferences(List<Retired> records, object? coreAllocator, Action<string> log)
    {
        int lines = 0;
        foreach (var r in records)
        {
            try
            {
                foreach (var member in r.RuntimeGenerics)
                {
                    log(CrossContextGenerics.DiagnosisLine(member));
                    lines++;
                }
                object? allocator = null;
                if (r.Allocator != null && r.Allocator.TryGetTarget(out var a)) allocator = a;
                int slot = CrossContextGenerics.FindReference(coreAllocator, allocator);
                if (slot >= 0)
                {
                    log(SlotLine(slot, r.Name));
                    lines++;
                }
            }
            catch
            {
                // Diagnostics only.
            }
        }
        return lines;
    }

    internal static string SlotLine(int slot, string context) =>
        "pin-diag plugin-holder <Core LoaderAllocator>.m_slots[" + slot.ToString(CultureInfo.InvariantCulture)
        + "] -> load context " + context + " (runtime cross-reference: a generic defined in the plugin "
        + "instantiated with Core types; released when this Core generation unloads)";

    /// <summary>
    /// Test seam for the runtime half of the diagnosis: record <paramref name="pluginType"/> as
    /// retired and report against <paramref name="coreType"/>'s LoaderAllocator, without the static walk.
    /// </summary>
    internal static int DiagnoseRuntimeReferences(Type pluginType, Type coreType, Action<string> log)
    {
        NoteUnloaded(pluginType, pluginType.FullName);
        List<(AssemblyLoadContext alc, string name, string plugin, Retired record)> targets = AliveTargets(0);
        var records = new List<Retired>();
        var alc = AssemblyLoadContext.GetLoadContext(pluginType.Assembly);
        foreach (var t in targets) if (ReferenceEquals(t.alc, alc)) records.Add(t.record);
        return ReportRuntimeReferences(records, CrossContextGenerics.LoaderAllocatorOf(coreType), log);
    }

    /// <summary>Recorded, still alive and older than <paramref name="minAgeSeconds"/>.</summary>
    private static List<(AssemblyLoadContext alc, string name, string plugin, Retired record)> AliveTargets(double minAgeSeconds)
    {
        var list = new List<(AssemblyLoadContext, string, string, Retired)>();
        long now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            Prune();
            foreach (var r in RetiredContexts)
            {
                if (!r.Context.TryGetTarget(out var alc)) continue;
                double age = (now - r.RetiredAt) / (double)Stopwatch.Frequency;
                if (age < minAgeSeconds) continue;
                list.Add((alc, r.Name, r.Plugin, r));
            }
        }
        return list;
    }

    private static void Prune()
    {
        for (int i = RetiredContexts.Count - 1; i >= 0; i--)
            if (!RetiredContexts[i].Context.TryGetTarget(out _)) RetiredContexts.RemoveAt(i);
    }

    private static Func<Assembly, bool> IsInAny(HashSet<AssemblyLoadContext> alcs) => asm =>
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(asm);
            return alc != null && alcs.Contains(alc);
        }
        catch { return false; }
    };

    private static void AddTypes(List<Type> into, Assembly asm)
    {
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types ?? Array.Empty<Type>(); }
        foreach (var t in types)
            if (t != null && !t.ContainsGenericParameters) into.Add(t);
    }

    // ------------------------------------------------------------------ walk

    private sealed class State
    {
        internal readonly Func<Type, bool> IsTargetType;
        internal readonly Func<Assembly, bool>? IsTargetAssembly;
        internal readonly Action<string> Log;
        internal Stopwatch Clock = Stopwatch.StartNew();
        internal readonly int Budget;
        internal readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<Type, bool> TargetTypes = new();
        internal readonly Dictionary<Type, FieldInfo[]> Fields = new();
        internal ICollection<AssemblyLoadContext>? TargetContexts;
        internal int Hits, Unprinted, Truncated;
        internal bool TimedOut;

        internal State(Func<Type, bool> isTargetType, Func<Assembly, bool>? isTargetAssembly, Action<string> log, int budget)
        {
            IsTargetType = isTargetType;
            IsTargetAssembly = isTargetAssembly;
            Log = log;
            Budget = budget;
        }

        internal bool OutOfTime()
        {
            if (TimedOut) return true;
            if (Clock.ElapsedMilliseconds < Budget) return false;
            TimedOut = true;
            return true;
        }
    }

    /// <summary>
    /// Walk the static fields of <paramref name="rootTypes"/> and log one <c>pin-diag plugin-holder</c>
    /// line per path that reaches a value whose type <paramref name="isTargetType"/> accepts (or a
    /// constructed generic or array over one), an assembly <paramref name="isTargetAssembly"/>
    /// accepts, or one of <paramref name="targetContexts"/> itself.
    /// Returns the number of holders found (printed or not).
    /// </summary>
    internal static int Walk(
        IReadOnlyList<Type> rootTypes,
        Func<Type, bool> isTargetType,
        Func<Assembly, bool>? isTargetAssembly,
        Action<string> log,
        int budgetMilliseconds,
        ICollection<AssemblyLoadContext>? targetContexts = null,
        Stopwatch? clock = null,
        Func<Type, bool>? mayRunStaticInit = null)
    {
        var state = new State(isTargetType, isTargetAssembly, log, budgetMilliseconds) { TargetContexts = targetContexts };
        if (clock != null) state.Clock = clock;
        var mayInit = mayRunStaticInit ?? IsRegistryNamespace;
        foreach (var type in rootTypes)
        {
            if (state.OutOfTime()) break;
            if (type == null || !ShouldReadStatics(type, mayInit)) continue;
            FieldInfo[] fields;
            try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var f in fields)
            {
                if (f.IsLiteral) continue;
                object? value;
                try { value = f.GetValue(null); }
                catch { continue; }
                Visit(value, Short(type) + "." + f.Name, state, 0);
            }
        }

        int ms = (int)state.Clock.ElapsedMilliseconds;
        log("pin-diag plugin-holders found=" + state.Hits.ToString(CultureInfo.InvariantCulture)
            + (state.Unprinted > 0 ? " unprinted=" + state.Unprinted.ToString(CultureInfo.InvariantCulture) : "")
            + (state.Truncated > 0 ? " truncatedArrays=" + state.Truncated.ToString(CultureInfo.InvariantCulture) : "")
            + (state.TimedOut ? " timedOut=true" : "")
            + " ms=" + ms.ToString(CultureInfo.InvariantCulture));
        return state.Hits;
    }

    /// <summary>
    /// Reading a static runs the type's static constructor if it has not run yet, and this walk runs in the middle of a
    /// Core swap. So a type with a static constructor is read only when <paramref name="mayRunStaticInit"/> allows it
    /// (by default: Core's and Bootstrap's registry namespaces, whose initializers only allocate managed tables) and none
    /// of its statics is a game type. A type without one is always safe to read.
    /// </summary>
    internal static bool ShouldReadStatics(Type type, Func<Type, bool>? mayRunStaticInit = null)
    {
        try
        {
            if (type.IsGenericTypeDefinition || type.ContainsGenericParameters) return false;
            if (type.TypeInitializer == null) return true;
            if (!(mayRunStaticInit ?? IsRegistryNamespace)(type)) return false;
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (IsGameType(f.FieldType)) return false;
            return true;
        }
        catch { return false; }
    }

    private static readonly string[] RegistryNamespaces =
    {
        "Progenitor.Ui", "Progenitor.Networking", "Progenitor.Config", "Progenitor.Dev", "Progenitor.Reload",
        "Progenitor.Plugins", "Progenitor.Events", "Progenitor.Hooks", "Progenitor.Persistence", "Progenitor.Commands",
        "Progenitor.Localization", "Progenitor.Systems", "Progenitor.Bootstrap",
    };

    /// <summary>Core and Bootstrap namespaces whose static initializers only build managed registries.</summary>
    internal static bool IsRegistryNamespace(Type type)
    {
        string ns = type.Namespace ?? "";
        if (ns == "Progenitor") return true;
        foreach (var r in RegistryNamespaces)
            if (ns == r || ns.StartsWith(r + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsGameType(Type t)
    {
        string ns = t.Namespace ?? "";
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("Unity.", StringComparison.Ordinal)
            || ns.StartsWith("ProjectM", StringComparison.Ordinal) || ns.StartsWith("Stunlock", StringComparison.Ordinal)
            || ns.StartsWith("Il2Cpp", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal))
            return true;
        return IsIl2CppWrapper(t);
    }

    private static bool IsIl2CppWrapper(Type? t)
    {
        for (; t != null && t != typeof(object); t = t.BaseType)
            if (t.Name == "Il2CppObjectBase") return true;
        return false;
    }

    private static void Visit(object? value, string path, State state, int depth)
    {
        if (value == null || state.OutOfTime()) return;
        Type type;
        try { type = value.GetType(); }
        catch { return; }
        if (type.IsPrimitive || type.IsEnum || type.IsPointer || value is string || value is decimal) return;

        if (value is Assembly asm)
        {
            if (SafeIsTarget(asm, state)) Report(path, "assembly " + SafeName(asm), state);
            return;
        }
        if (value is Type t)
        {
            if (TypeIsTarget(t, state)) Report(path, "type " + t.FullName, state);
            return;
        }
        if (value is AssemblyLoadContext alc)
        {
            if ((state.TargetContexts != null && state.TargetContexts.Contains(alc)) || TypeIsTarget(type, state))
                Report(path, "load context " + alc.Name, state);
            return;
        }
        if (value is MemberInfo member)
        {
            if (member.DeclaringType != null && TypeIsTarget(member.DeclaringType, state))
                Report(path, "member " + member.DeclaringType.FullName + "." + member.Name, state);
            return;
        }
        if (value is WeakReference || IsWeakType(type)) return;

        if (TypeIsTarget(type, state))
        {
            Report(path, type.FullName ?? type.Name, state);
            return; // one line per path into the plugin; its own graph is the plugin's business
        }

        if (type.IsValueType)
        {
            // Boxed per read, so identity is meaningless: walk fields without the seen set.
            if (depth < MaxDepth) WalkFields(value, type, path, state, depth + 1);
            return;
        }

        if (!state.Seen.Add(value)) return;
        if (IsIl2CppWrapper(type)) return; // a native pointer; never walk a wrapper on a diagnostic path
        if (depth >= MaxDepth) return;

        if (value is Delegate d)
        {
            Delegate[] list;
            try { list = d.GetInvocationList(); }
            catch { list = new[] { d }; }
            for (int i = 0; i < list.Length; i++)
            {
                var one = list[i];
                string at = list.Length > 1 ? path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]" : path;
                MethodInfo? m = null;
                try { m = one.Method; }
                catch { /* */ }
                if (m?.DeclaringType != null && TypeIsTarget(m.DeclaringType, state))
                {
                    Report(at, "delegate " + m.DeclaringType.FullName + "." + m.Name, state);
                    continue;
                }
                object? target = null;
                try { target = one.Target; }
                catch { /* */ }
                Visit(target, at + ".Target", state, depth + 1);
            }
            return;
        }

        if (value is Array array)
        {
            var element = type.GetElementType();
            if (element != null && (element.IsPrimitive || element.IsEnum || element == typeof(string))) return;
            int n = array.Rank == 1 ? array.Length : 0;
            if (n > MaxArrayElements) { state.Truncated++; n = MaxArrayElements; }
            for (int i = 0; i < n; i++)
            {
                if (state.OutOfTime()) return;
                object? item;
                try { item = array.GetValue(i); }
                catch { continue; }
                Visit(item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", state, depth + 1);
            }
            return;
        }

        WalkFields(value, type, path, state, depth + 1);
    }

    private static void WalkFields(object value, Type type, string path, State state, int depth)
    {
        foreach (var f in InstanceFields(type, state))
        {
            if (state.OutOfTime()) return;
            object? child;
            try { child = f.GetValue(value); }
            catch { continue; }
            Visit(child, path + "." + f.Name, state, depth);
        }
    }

    private static FieldInfo[] InstanceFields(Type type, State state)
    {
        if (state.Fields.TryGetValue(type, out var cached)) return cached;
        var list = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo[] fs;
            try { fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { break; }
            foreach (var f in fs)
            {
                var ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft.IsPointer || ft == typeof(string) || ft == typeof(IntPtr)) continue;
                list.Add(f);
            }
        }
        var result = list.ToArray();
        state.Fields[type] = result;
        return result;
    }

    /// <summary>A type from a target assembly, or a constructed generic or array over one.</summary>
    private static bool TypeIsTarget(Type type, State state)
    {
        if (state.TargetTypes.TryGetValue(type, out var known)) return known;
        bool hit = false;
        try
        {
            if (type.HasElementType) hit = TypeIsTarget(type.GetElementType()!, state);
            else
            {
                try { hit = state.IsTargetType(type); }
                catch { hit = false; }
                if (!hit && type.IsGenericType)
                    foreach (var arg in type.GetGenericArguments())
                        if (TypeIsTarget(arg, state)) { hit = true; break; }
            }
        }
        catch { hit = false; }
        state.TargetTypes[type] = hit;
        return hit;
    }

    private static bool SafeIsTarget(Assembly asm, State state)
    {
        try { return state.IsTargetAssembly?.Invoke(asm) ?? false; }
        catch { return false; }
    }

    private static bool IsWeakType(Type type)
    {
        if (!type.IsGenericType) return false;
        var def = type.GetGenericTypeDefinition();
        // A weak table is matched by name: declaring one here is reserved for reviewed files.
        return def == typeof(WeakReference<>)
            || (def.Namespace == "System.Runtime.CompilerServices" && def.Name.StartsWith("ConditionalWeakTable", StringComparison.Ordinal));
    }

    private static void Report(string path, string what, State state)
    {
        state.Hits++;
        if (state.Hits > MaxHits) { state.Unprinted++; return; }
        state.Log("pin-diag plugin-holder " + path + " -> " + what);
    }

    private static string Short(Type type)
    {
        string ns = type.Namespace ?? "";
        string name = type.FullName ?? type.Name;
        if (ns.StartsWith("Progenitor.", StringComparison.Ordinal)) name = name.Substring("Progenitor.".Length);
        return name.Replace('+', '.');
    }

    private static string SafeName(Assembly asm)
    {
        try { return asm.GetName().Name ?? "?"; }
        catch { return "?"; }
    }
}
