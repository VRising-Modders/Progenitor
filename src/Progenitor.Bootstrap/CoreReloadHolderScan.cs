using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Progenitor.Bootstrap;

/// <summary>
/// Reflective "who holds the unloading generation" walk. Run immediately before
/// <c>ALC.Unload</c> after published Core refs and slots have been dropped: any remaining
/// hit is a Bootstrap static / orchestrator / loader / frame-driver / slot field that will
/// keep generation N alive across the collect poll (evidence 61: gen 1 collects, gen 2
/// does not). Bounded depth, cycle-safe, try/catch per field. Game-free so the walker
/// is unit-tested without BepInEx.
/// </summary>
public static class CoreReloadHolderScan
{
    /// <summary>Field-graph depth for the plain scan. Roots are depth 0; deeper than this is truncated.</summary>
    public const int MaxDepth = 4;

    /// <summary>Cap so a huge graph cannot flood the log. Remaining hits are counted.</summary>
    public const int MaxHits = 32;

    /// <summary>Wide-scan depth. A client pin outside Bootstrap statics needs one extra hop.</summary>
    public const int WideMaxDepth = 6;

    /// <summary>Wide-scan hit cap. The freeze hitch is also time-boxed by <see cref="HolderScanBudgetMilliseconds"/>.</summary>
    public const int WideMaxHits = 64;

    /// <summary>
    /// Hard cap for the whole scan (plain or wide) on the freeze. Stop and log
    /// <c>pin-diag holder-scan truncated ms=N</c> when elapsed reaches this.
    /// </summary>
    public const int HolderScanBudgetMilliseconds = 400;

    /// <summary>
    /// Options for <see cref="Walk"/>. Plain is Bootstrap statics plus caller roots.
    /// Wide enumerates statics of allowlisted non-collectible assemblies.
    /// </summary>
    public sealed class Options
    {
        public bool Wide { get; init; }
        public int MaxDepth { get; init; } = CoreReloadHolderScan.MaxDepth;
        public int MaxHits { get; init; } = CoreReloadHolderScan.MaxHits;
        public int BudgetMilliseconds { get; init; } = HolderScanBudgetMilliseconds;
        public IReadOnlyList<Assembly>? Assemblies { get; init; }

        public static Options Plain { get; } = new();

        public static Options WideScan { get; } = new()
        {
            Wide = true,
            MaxDepth = WideMaxDepth,
            MaxHits = WideMaxHits,
            BudgetMilliseconds = HolderScanBudgetMilliseconds,
        };
    }

    /// <summary>
    /// Walk <paramref name="staticTypes"/>' static fields and <paramref name="roots"/>'
    /// instance fields. Logs <see cref="CoreReloadPolicy.FormatHolder"/> for every value
    /// whose runtime type lives in the unloading ALC/assembly, or that <em>is</em> that
    /// ALC/assembly, or a delegate whose method/target does.
    /// </summary>
    public static void Walk(
        IReadOnlyList<Type>? staticTypes,
        IReadOnlyList<object?>? roots,
        Assembly? unloadingAssembly,
        AssemblyLoadContext? unloadingAlc,
        Action<string> log)
        => Walk(staticTypes, roots, unloadingAssembly, unloadingAlc, log, Options.Plain);

    public static void Walk(
        IReadOnlyList<Type>? staticTypes,
        IReadOnlyList<object?>? roots,
        Assembly? unloadingAssembly,
        AssemblyLoadContext? unloadingAlc,
        Action<string> log,
        Options? options)
    {
        if (log == null) throw new ArgumentNullException(nameof(log));
        options ??= Options.Plain;
        var clock = Stopwatch.StartNew();
        var state = new ScanState(
            unloadingAssembly,
            unloadingAlc,
            log,
            options.Wide,
            options.MaxDepth < 0 ? 0 : options.MaxDepth,
            options.MaxHits < 1 ? 1 : options.MaxHits,
            clock,
            options.BudgetMilliseconds < 0 ? 0 : options.BudgetMilliseconds);

        if (options.Wide)
        {
            var assemblies = options.Assemblies ?? CollectWideAssemblies();
            bool applyAllowlist = options.Assemblies == null;
            WalkAssemblies(assemblies, state, applyAllowlist);
        }

        if (staticTypes != null)
        {
            for (int i = 0; i < staticTypes.Count && !state.TimedOut; i++)
            {
                var type = staticTypes[i];
                if (type == null) continue;
                try
                {
                    WalkStatic(type, type.Name, state, depth: 0);
                }
                catch (Exception e)
                {
                    log("pin-diag holder-scan " + type.Name + ": " + e.Message);
                }
            }
        }

        if (roots != null)
        {
            for (int i = 0; i < roots.Count && !state.TimedOut; i++)
            {
                object? root = roots[i];
                if (root == null) continue;
                string name;
                try { name = root.GetType().Name; }
                catch { name = "root" + i.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                try
                {
                    Visit(root, name, state, depth: 0);
                }
                catch (Exception e)
                {
                    log("pin-diag holder-scan " + name + ": " + e.Message);
                }
            }
        }

        clock.Stop();
        int ms = (int)clock.ElapsedMilliseconds;
        if (state.Hits == 0)
            log("pin-diag holders=0");
        else if (state.Truncated > 0)
            log("pin-diag holders=" + state.Hits.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " truncated=" + state.Truncated.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (state.TimedOut)
            log(CoreReloadPolicy.FormatHolderScanTruncated(ms));
        log(CoreReloadPolicy.FormatHolderScanElapsed(ms, options.Wide));
    }

    /// <summary>
    /// Wide scan: statics of every type in <paramref name="assemblies"/> (or every
    /// non-collectible ALC assembly when null) plus <paramref name="extraRoots"/>.
    /// </summary>
    public static void WalkWide(
        IReadOnlyList<Assembly>? assemblies,
        IReadOnlyList<object?>? extraRoots,
        Assembly? unloadingAssembly,
        AssemblyLoadContext? unloadingAlc,
        Action<string> log)
        => Walk(
            staticTypes: null,
            roots: extraRoots,
            unloadingAssembly,
            unloadingAlc,
            log,
            new Options
            {
                Wide = true,
                MaxDepth = WideMaxDepth,
                MaxHits = WideMaxHits,
                Assemblies = assemblies,
            });

    /// <summary>
    /// Assemblies whose statics a wide scan may walk when the caller did not pass
    /// an explicit list. Allowlist: <c>BepInEx*</c>, <c>Il2CppInterop.Runtime</c>,
    /// <c>0Harmony</c>, <c>MonoMod.*</c>, <c>Progenitor.Bootstrap</c>. Chainloader
    /// plugins under <c>BepInEx/plugins</c> are not scanned by default (R17
    /// finding 6); pass them via <see cref="Options.Assemblies"/>. Never
    /// ProjectM / Unity / Il2Cpp* interop proxies. Collectible ALCs are skipped
    /// (the unloading Core generation is one of those).
    /// </summary>
    public static bool IsWideScanAssembly(Assembly? assembly)
    {
        if (assembly == null) return false;
        if (IsCollectibleAssembly(assembly)) return false;
        string name;
        try { name = assembly.GetName().Name ?? ""; }
        catch { return false; }
        string location;
        try { location = assembly.Location ?? ""; }
        catch { location = ""; }
        return IsWideScanAssemblyName(name, location);
    }

    /// <summary>
    /// Name/location half of <see cref="IsWideScanAssembly"/> so tests do not need
    /// a live BepInEx folder.
    /// </summary>
    public static bool IsWideScanAssemblyName(string? name, string? location)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (IsFrameworkAssemblyName(name)) return false;
        if (IsIl2CppInteropProxyName(name)) return false;
        if (string.Equals(name, "0Harmony", StringComparison.Ordinal)) return true;
        if (string.Equals(name, "Il2CppInterop.Runtime", StringComparison.Ordinal)) return true;
        if (string.Equals(name, "Progenitor.Bootstrap", StringComparison.Ordinal)) return true;
        if (string.Equals(name, "BepInEx", StringComparison.Ordinal)
            || name.StartsWith("BepInEx.", StringComparison.Ordinal))
            return true;
        if (string.Equals(name, "MonoMod", StringComparison.Ordinal)
            || name.StartsWith("MonoMod.", StringComparison.Ordinal))
            return true;
        _ = location;
        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> may have its static fields <c>GetValue</c>'d.
    /// A type with a static constructor is skipped unless it is Bootstrap's own or
    /// one of the known-initialized Il2CppInterop / BepInEx logger tables. There is
    /// no public <c>RuntimeHelpers</c> "already initialized" probe; running the
    /// cctor from the freeze is the hitch/crash this rule exists to avoid.
    /// </summary>
    public static bool ShouldReadStaticFields(Type? type)
    {
        if (type == null) return false;
        ConstructorInfo? cctor;
        try { cctor = type.TypeInitializer; }
        catch { return false; }
        if (cctor == null) return true;
        if (IsBootstrapOwnType(type)) return true;
        return IsKnownInitializedStaticType(type);
    }

    /// <summary>
    /// Types whose static constructor is known to have already run by the time a
    /// Core swap freeze walks them. The cctor skip must not skip the Harmony /
    /// BepInEx tables the wide scan exists to name (R17 finding 6).
    /// </summary>
    public static bool IsKnownInitializedStaticType(Type? type)
    {
        if (type == null) return false;
        string? n;
        try { n = type.FullName; }
        catch { return false; }
        return IsKnownInitializedStaticTypeName(n);
    }

    /// <summary>
    /// Name half of <see cref="IsKnownInitializedStaticType"/> so tests do not
    /// need Harmony or BepInEx on the reference list.
    /// </summary>
    public static bool IsKnownInitializedStaticTypeName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return false;
        return string.Equals(fullName, "Il2CppInterop.Runtime.DelegateSupport", StringComparison.Ordinal)
            || string.Equals(fullName, "Il2CppInterop.Runtime.Injection.ClassInjector", StringComparison.Ordinal)
            || string.Equals(fullName, "Il2CppInterop.Runtime.Runtime.Il2CppObjectPool", StringComparison.Ordinal)
            || string.Equals(fullName, "BepInEx.Logging.Logger", StringComparison.Ordinal)
            || string.Equals(fullName, "BepInEx.Configuration.ConfigFile", StringComparison.Ordinal)
            || string.Equals(fullName, "HarmonyLib.HarmonySharedState", StringComparison.Ordinal)
            || string.Equals(fullName, "HarmonyLib.PatchFunctions", StringComparison.Ordinal)
            || string.Equals(fullName, "HarmonyLib.Internal.Patching.PatchFunctions", StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="type"/> (or a base) is Il2CppObjectBase.</summary>
    public static bool IsIl2CppObjectBaseType(Type? type)
    {
        Type? t = type;
        while (t != null && t != typeof(object))
        {
            string? full;
            string? name;
            try
            {
                full = t.FullName;
                name = t.Name;
            }
            catch
            {
                return false;
            }
            if (string.Equals(full, "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", StringComparison.Ordinal)
                || string.Equals(name, "Il2CppObjectBase", StringComparison.Ordinal))
                return true;
            try { t = t.BaseType; }
            catch { return false; }
        }
        return false;
    }

    private static bool IsBootstrapOwnType(Type type)
    {
        try
        {
            string? name = type.Assembly.GetName().Name;
            return string.Equals(name, "Progenitor.Bootstrap", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFrameworkAssemblyName(string name) =>
        string.Equals(name, "System", StringComparison.Ordinal)
        || name.StartsWith("System.", StringComparison.Ordinal)
        || string.Equals(name, "Microsoft", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || string.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "mscorlib", StringComparison.OrdinalIgnoreCase);

    private static bool IsIl2CppInteropProxyName(string name) =>
        name.StartsWith("Il2Cpp", StringComparison.Ordinal)
        && !string.Equals(name, "Il2CppInterop.Runtime", StringComparison.Ordinal)
        && !name.StartsWith("Il2CppInterop.Runtime", StringComparison.Ordinal);

    /// <summary>True when <paramref name="location"/> sits under <c>BepInEx/plugins</c>.</summary>
    public static bool IsBepInExPluginLocation(string? location)
    {
        if (string.IsNullOrEmpty(location)) return false;
        string n = location.Replace('/', '\\');
        return n.IndexOf("\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCollectibleAssembly(Assembly assembly)
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(assembly);
            return alc != null && alc.IsCollectible;
        }
        catch
        {
            return true;
        }
    }

    private static List<Assembly> CollectWideAssemblies()
    {
        var list = new List<Assembly>();
        Assembly[] loaded;
        try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return list; }
        for (int i = 0; i < loaded.Length; i++)
        {
            var asm = loaded[i];
            if (!IsWideScanAssembly(asm)) continue;
            list.Add(asm);
        }
        return list;
    }

    private static void WalkAssemblies(IReadOnlyList<Assembly> assemblies, ScanState state, bool applyAllowlist)
    {
        for (int a = 0; a < assemblies.Count; a++)
        {
            if (state.CheckTimeout()) break;
            var asm = assemblies[a];
            if (asm == null) continue;
            if (IsCollectibleAssembly(asm)) continue;
            if (applyAllowlist)
            {
                if (!IsWideScanAssembly(asm)) continue;
            }
            else
            {
                string name;
                try { name = asm.GetName().Name ?? ""; }
                catch { name = ""; }
                if (IsFrameworkAssemblyName(name)) continue;
            }
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types ?? Array.Empty<Type>();
            }
            catch
            {
                continue;
            }
            string asmName;
            try { asmName = asm.GetName().Name ?? asm.FullName ?? "?"; }
            catch { asmName = "?"; }
            for (int t = 0; t < types.Length; t++)
            {
                if (state.CheckTimeout()) return;
                var type = types[t];
                if (type == null) continue;
                try
                {
                    WalkStatic(type, asmName + ":" + type.Name, state, depth: 0);
                }
                catch
                {
                    // one type must not abort the scan
                }
            }
        }
    }

    private static void WalkStatic(Type type, string path, ScanState state, int depth)
    {
        if (state.CheckTimeout()) return;
        if (!ShouldReadStaticFields(type)) return;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type? cursor = type;
        while (cursor != null && cursor != typeof(object))
        {
            if (state.CheckTimeout()) return;
            FieldInfo[] fields;
            try { fields = cursor.GetFields(flags); }
            catch
            {
                break;
            }
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field == null || field.IsLiteral) continue;
                object? value;
                try { value = field.GetValue(null); }
                catch { continue; }
                Visit(value, path + "." + field.Name, state, depth);
            }
            cursor = cursor.BaseType;
        }
    }

    private static void Visit(object? value, string path, ScanState state, int depth)
    {
        if (value == null) return;
        if (state.CheckTimeout()) return;
        if (depth > state.MaxDepth) return;
        if (IsLeaf(value)) return;
        if (value is WeakReference || IsGenericWeak(value)) return;

        // AppDomain.GetAssemblies / AssemblyLoadContext.All still list the
        // unloading identity until ALC.Unload (the next line of UnloadAlc).
        // Those listings are expected, not holders. A field that *points at*
        // the ALC/assembly is reported below; listing membership is not.
        if (IsLoadedContextListing(value))
        {
            if (!state.Seen.Add(value)) return;
            WalkListingWithoutUnloadingIdentity(value, path, state, depth);
            return;
        }

        if (value is AppDomain)
        {
            if (!state.Seen.Add(value)) return;
            WalkAppDomainSkippingListings(value, path, state, depth);
            return;
        }

        bool unloading = IsUnloading(value, state.UnloadingAssembly, state.UnloadingAlc);
        if (unloading)
            Report(path, value, state);

        if (value is Assembly or AssemblyLoadContext or MemberInfo or Type)
            return;

        if (IsIl2CppObjectBaseType(SafeGetType(value)))
        {
            // Record type + pointer only (Report already ran when unloading).
            // Never WalkInstance / foreach a wrapper whose native pointer may
            // already be dead on the freeze.
            state.Seen.Add(value);
            return;
        }

        if (!state.Seen.Add(value)) return;
        if (depth == state.MaxDepth) return;

        if (value is Delegate d)
        {
            VisitDelegate(d, path, state, depth);
            return;
        }

        if (IsConditionalWeakTable(value))
        {
            VisitEnumerable(value as IEnumerable ?? Empty, path, state, depth);
            return;
        }

        if (!ShouldEnumerate(value))
        {
            WalkInstance(value, path, state, depth + 1);
            return;
        }

        if (value is IDictionary dict)
        {
            VisitDictionary(dict, path, state, depth);
            return;
        }

        if (value is IEnumerable en and not string)
        {
            VisitEnumerable(en, path, state, depth);
            return;
        }

        WalkInstance(value, path, state, depth + 1);
    }

    private static Type? SafeGetType(object value)
    {
        try { return value.GetType(); }
        catch { return null; }
    }

    /// <summary>
    /// AppDomain loaded-assembly storage and <c>AssemblyLoadContext.All</c> (and
    /// similar enumerables of <see cref="Assembly"/> / <see cref="AssemblyLoadContext"/>).
    /// Presence of the unloading identity in those lists is expected until Unload.
    /// </summary>
    public static bool IsLoadedContextListing(object? value)
    {
        if (value == null) return false;
        if (value is Assembly[]) return true;
        if (value is AssemblyLoadContext[]) return true;
        if (value is IEnumerable<Assembly>) return true;
        if (value is IEnumerable<AssemblyLoadContext>) return true;
        return false;
    }

    private static void WalkListingWithoutUnloadingIdentity(object listing, string path, ScanState state, int depth)
    {
        if (listing is not IEnumerable en) return;
        int n = 0;
        try
        {
            foreach (var item in en)
            {
                if (state.CheckTimeout()) return;
                if (n++ >= state.MaxHits) break;
                if (item == null) continue;
                if (item is Assembly or AssemblyLoadContext)
                {
                    // Expected until ALC.Unload. Not a holder.
                    continue;
                }
                Visit(item, path + "[]", state, depth + 1);
            }
        }
        catch
        {
            // enumerator threw
        }
    }

    private static void WalkAppDomainSkippingListings(object domain, string path, ScanState state, int depth)
    {
        Type type;
        try { type = domain.GetType(); }
        catch { return; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type? cursor = type;
        int types = 0;
        while (cursor != null && cursor != typeof(object) && types++ < 8)
        {
            FieldInfo[] fields;
            try { fields = cursor.GetFields(flags); }
            catch { break; }
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field == null) continue;
                if (IsLoadedAssemblyStorageField(field)) continue;
                object? child;
                try { child = field.GetValue(domain); }
                catch { continue; }
                Visit(child, path + "." + field.Name, state, depth);
            }
            cursor = cursor.BaseType;
        }
    }

    private static bool IsLoadedAssemblyStorageField(FieldInfo field)
    {
        Type ft;
        try { ft = field.FieldType; }
        catch { return false; }
        if (ft == typeof(Assembly[]) || ft == typeof(AssemblyLoadContext[])) return true;
        if (typeof(IEnumerable<Assembly>).IsAssignableFrom(ft)) return true;
        if (typeof(IEnumerable<AssemblyLoadContext>).IsAssignableFrom(ft)) return true;
        return false;
    }

    /// <summary>
    /// Do not <c>foreach</c> an enumerable whose type lives in an Il2Cpp* interop
    /// proxy (native MoveNext). Known managed Il2CppInterop tables are allowed.
    /// </summary>
    public static bool ShouldEnumerate(object? value)
    {
        if (value == null) return false;
        Type type;
        try { type = value.GetType(); }
        catch { return false; }
        if (IsIl2CppObjectBaseType(type)) return false;
        string? asmName;
        try { asmName = type.Assembly.GetName().Name; }
        catch { return false; }
        if (string.IsNullOrEmpty(asmName)) return true;
        if (IsIl2CppInteropProxyName(asmName)) return false;
        if (string.Equals(asmName, "Il2CppInterop.Runtime", StringComparison.Ordinal)
            || asmName.StartsWith("Il2CppInterop.Runtime", StringComparison.Ordinal))
            return IsKnownInitializedStaticType(type.DeclaringType ?? type);
        return true;
    }

    private static readonly object[] EmptyArray = Array.Empty<object>();
    private static IEnumerable Empty => EmptyArray;

    private static void VisitDelegate(Delegate d, string path, ScanState state, int depth)
    {
        Delegate[] list;
        try { list = d.GetInvocationList(); }
        catch
        {
            list = new[] { d };
        }
        for (int i = 0; i < list.Length; i++)
        {
            var item = list[i];
            if (item == null) continue;
            try
            {
                var method = item.Method;
                if (method != null && IsUnloadingType(method.DeclaringType, state.UnloadingAssembly, state.UnloadingAlc))
                    Report(path + ".Method", method, state);
            }
            catch
            {
                // Il2CppInterop trampoline .Method can throw.
            }
            try
            {
                Visit(item.Target, path + ".Target", state, depth + 1);
            }
            catch
            {
                // per-item
            }
        }
    }

    private static void VisitDictionary(IDictionary dict, string path, ScanState state, int depth)
    {
        int n = 0;
        try
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (state.CheckTimeout()) return;
                if (n++ >= state.MaxHits) break;
                Visit(entry.Key, path + ".Key", state, depth + 1);
                Visit(entry.Value, path + ".Value", state, depth + 1);
            }
        }
        catch
        {
            // enumerator threw
        }
    }

    private static void VisitEnumerable(IEnumerable en, string path, ScanState state, int depth)
    {
        int n = 0;
        try
        {
            foreach (var item in en)
            {
                if (state.CheckTimeout()) return;
                if (n++ >= state.MaxHits) break;
                Visit(item, path + "[]", state, depth + 1);
            }
        }
        catch
        {
            // enumerator threw
        }
    }

    private static void WalkInstance(object value, string path, ScanState state, int depth)
    {
        if (state.CheckTimeout()) return;
        Type type;
        try { type = value.GetType(); }
        catch { return; }
        if (type.IsPrimitive || type.IsEnum || type.IsPointer) return;
        if (IsIl2CppObjectBaseType(type)) return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type? cursor = type;
        int types = 0;
        while (cursor != null && cursor != typeof(object) && types++ < 8)
        {
            FieldInfo[] fields;
            try { fields = cursor.GetFields(flags); }
            catch { break; }
            for (int i = 0; i < fields.Length; i++)
            {
                if (state.CheckTimeout()) return;
                var field = fields[i];
                if (field == null) continue;
                object? child;
                try { child = field.GetValue(value); }
                catch { continue; }
                Visit(child, path + "." + field.Name, state, depth);
            }
            cursor = cursor.BaseType;
        }
    }

    private static void Report(string path, object value, ScanState state)
    {
        state.Hits++;
        if (state.Hits > state.MaxHits)
        {
            state.Truncated++;
            return;
        }
        string typeName;
        try { typeName = value.GetType().FullName ?? value.GetType().Name; }
        catch { typeName = "?"; }
        typeName = AppendIl2CppPointer(typeName, value);
        try
        {
            if (state.Wide)
            {
                string assembly = AssemblyNameOf(path);
                string fieldPath = FieldPathOf(path, assembly);
                state.Log(CoreReloadPolicy.FormatHolder(assembly, fieldPath, typeName));
            }
            else
                state.Log(CoreReloadPolicy.FormatHolder(path, typeName));
        }
        catch { /* never throw from pin-diag */ }
    }

    private static string AppendIl2CppPointer(string typeName, object value)
    {
        if (!IsIl2CppObjectBaseType(SafeGetType(value))) return typeName;
        try
        {
            var prop = value.GetType().GetProperty("Pointer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? raw = prop?.GetValue(value);
            if (raw is IntPtr ip)
                return typeName + " ptr=0x" + ip.ToInt64().ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            if (raw is UIntPtr up)
                return typeName + " ptr=0x" + up.ToUInt64().ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            // pointer unreadable; type name is enough
        }
        return typeName;
    }

    private static string AssemblyNameOf(string path)
    {
        int colon = path.IndexOf(':');
        if (colon <= 0) return "";
        return path.Substring(0, colon);
    }

    private static string FieldPathOf(string path, string assembly)
    {
        if (string.IsNullOrEmpty(assembly)) return path;
        if (path.Length > assembly.Length && path[assembly.Length] == ':')
            return path.Substring(assembly.Length + 1);
        return path;
    }

    private static bool IsUnloading(object value, Assembly? unloadingAssembly, AssemblyLoadContext? unloadingAlc)
    {
        if (unloadingAlc != null && ReferenceEquals(value, unloadingAlc))
            return true;
        if (unloadingAssembly != null && ReferenceEquals(value, unloadingAssembly))
            return true;
        Type type;
        try { type = value.GetType(); }
        catch { return false; }
        if (IsUnloadingType(type, unloadingAssembly, unloadingAlc))
            return true;
        if (value is Delegate d)
        {
            try
            {
                if (IsUnloadingType(d.Method?.DeclaringType, unloadingAssembly, unloadingAlc))
                    return true;
            }
            catch { /* trampoline */ }
            try
            {
                if (d.Target != null && IsUnloading(d.Target, unloadingAssembly, unloadingAlc))
                    return true;
            }
            catch { /* trampoline */ }
        }
        if (value is MemberInfo mi)
        {
            try
            {
                if (IsUnloadingType(mi.DeclaringType, unloadingAssembly, unloadingAlc))
                    return true;
            }
            catch { /* */ }
        }
        return false;
    }

    private static bool IsUnloadingType(Type? type, Assembly? unloadingAssembly, AssemblyLoadContext? unloadingAlc)
    {
        if (type == null) return false;
        Assembly? asm;
        try { asm = type.Assembly; }
        catch { return false; }
        if (unloadingAssembly != null && ReferenceEquals(asm, unloadingAssembly))
            return true;
        if (unloadingAlc == null || asm == null) return false;
        try
        {
            return ReferenceEquals(AssemblyLoadContext.GetLoadContext(asm), unloadingAlc);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLeaf(object value)
    {
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type.IsPointer) return true;
        if (value is string or decimal or IntPtr or UIntPtr) return true;
        if (value is byte[]) return true;
        return false;
    }

    private static bool IsGenericWeak(object value)
    {
        Type type;
        try { type = value.GetType(); }
        catch { return false; }
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(WeakReference<>);
    }

    private static bool IsConditionalWeakTable(object value)
    {
        Type type;
        try { type = value.GetType(); }
        catch { return false; }
        while (type != null && type != typeof(object))
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
                return true;
            type = type.BaseType!;
        }
        return false;
    }

    private sealed class ScanState
    {
        public ScanState(
            Assembly? unloadingAssembly,
            AssemblyLoadContext? unloadingAlc,
            Action<string> log,
            bool wide,
            int maxDepth,
            int maxHits,
            Stopwatch clock,
            int budgetMilliseconds)
        {
            UnloadingAssembly = unloadingAssembly;
            UnloadingAlc = unloadingAlc;
            Log = log;
            Wide = wide;
            MaxDepth = maxDepth;
            MaxHits = maxHits;
            Clock = clock;
            BudgetMilliseconds = budgetMilliseconds;
            Seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        }

        public Assembly? UnloadingAssembly { get; }
        public AssemblyLoadContext? UnloadingAlc { get; }
        public Action<string> Log { get; }
        public bool Wide { get; }
        public int MaxDepth { get; }
        public int MaxHits { get; }
        public Stopwatch Clock { get; }
        public int BudgetMilliseconds { get; }
        public HashSet<object> Seen { get; }
        public int Hits;
        public int Truncated;
        public bool TimedOut;

        public bool CheckTimeout()
        {
            if (TimedOut) return true;
            if (BudgetMilliseconds < 0) return false;
            if (Clock.ElapsedMilliseconds < BudgetMilliseconds) return false;
            TimedOut = true;
            return true;
        }
    }
}
