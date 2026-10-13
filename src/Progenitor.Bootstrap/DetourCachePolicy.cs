using System;
using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free rules for BepInEx.Unity.IL2CPP's append-only
/// <c>Il2CppDetourMethodPatcher.DelegateCache</c>. Unpatch leaves the trampoline
/// in that list, so a patch <c>MethodInfo</c> (or its target) that lives in a
/// collectible ALC pins the context forever. Tests feed a fake cache of
/// <see cref="Entry"/> values; live code fills those from the real list.
/// </summary>
public static class DetourCachePolicy
{
    /// <summary>
    /// Grep line when a plugin ALC's methods are still in the IL2CPP detour cache
    /// after Unload. The reloader must not wait <see cref="CoreReloadPolicy.PluginAlcWaitFrames"/>
    /// for a collect that cannot happen.
    /// </summary>
    public static string FormatPluginAlcPinned(string? alcName, string? method) =>
        "plugin-alc " + (alcName ?? "") + ": pinned by IL2CPP detour cache: " + (method ?? "");

    /// <summary>
    /// Summary line. <paramref name="core"/> is entries whose declaring assembly is
    /// the Core generation (historical grep). <paramref name="collectible"/> is every
    /// entry whose method or target type lives in any collectible context.
    /// </summary>
    public static string FormatDelegateCacheLog(int count, int core, int collectible) =>
        "pin-diag HarmonyIL2CPP DelegateCache count=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " core=" + core.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " collectible=" + collectible.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One cache row: the collectible context name and the patched method.</summary>
    public static string FormatCacheEntry(string? alcName, string? method) =>
        "  cache alc=" + (alcName ?? CoreReloadPolicy.UnnamedAlcName) + " method=" + (method ?? "");

    /// <summary>True when this cache entry must keep a collectible ALC alive.</summary>
    public static bool IsCollectiblePin(in Entry entry, out string alcName, out string method)
    {
        method = MethodLabel(entry);
        if (entry.MethodAlcCollectible)
        {
            alcName = NamedAlc(entry.MethodAlcName);
            return true;
        }
        if (entry.TargetAlcCollectible)
        {
            alcName = NamedAlc(entry.TargetAlcName);
            return true;
        }
        alcName = "";
        return false;
    }

    /// <summary>
    /// True when this pin belongs to the plugin assembly / ALC being unloaded
    /// (method declaring type or delegate target).
    /// </summary>
    public static bool PinsContext(in Entry entry, string? assemblyName, string? alcName)
    {
        if (!IsCollectiblePin(entry, out var pinAlc, out _))
            return false;
        if (!string.IsNullOrEmpty(alcName)
            && (string.Equals(pinAlc, alcName, StringComparison.Ordinal)
                || string.Equals(entry.MethodAlcName, alcName, StringComparison.Ordinal)
                || string.Equals(entry.TargetAlcName, alcName, StringComparison.Ordinal)))
            return true;
        if (!string.IsNullOrEmpty(assemblyName)
            && (string.Equals(entry.MethodAssemblyName, assemblyName, StringComparison.Ordinal)
                || string.Equals(entry.TargetAssemblyName, assemblyName, StringComparison.Ordinal)))
            return true;
        return false;
    }

    /// <summary>
    /// Walk a fake or live cache. Appends <see cref="FormatCacheEntry"/> lines for
    /// every collectible pin (capped at <paramref name="sampleCap"/>).
    /// </summary>
    public static int DescribeCollectible(
        IReadOnlyList<Entry>? cache,
        List<string> lines,
        int sampleCap = 16)
    {
        if (lines == null) throw new ArgumentNullException(nameof(lines));
        if (cache == null || cache.Count == 0) return 0;
        int n = 0;
        for (int i = 0; i < cache.Count; i++)
        {
            var entry = cache[i];
            if (!IsCollectiblePin(entry, out var alc, out var method)) continue;
            n++;
            if (lines.Count < sampleCap)
                lines.Add(FormatCacheEntry(alc, method));
        }
        return n;
    }

    /// <summary>
    /// Methods from <paramref name="cache"/> that pin <paramref name="assemblyName"/> /
    /// <paramref name="alcName"/>. Used by the plugin reloader so a cache hit can skip
    /// the 60-frame collect wait.
    /// </summary>
    public static List<string> MethodsPinningContext(
        IReadOnlyList<Entry>? cache,
        string? assemblyName,
        string? alcName)
    {
        var methods = new List<string>();
        if (cache == null || cache.Count == 0) return methods;
        for (int i = 0; i < cache.Count; i++)
        {
            var entry = cache[i];
            if (!PinsContext(entry, assemblyName, alcName)) continue;
            string method = MethodLabel(entry);
            bool dup = false;
            for (int j = 0; j < methods.Count; j++)
            {
                if (string.Equals(methods[j], method, StringComparison.Ordinal))
                {
                    dup = true;
                    break;
                }
            }
            if (!dup) methods.Add(method);
        }
        return methods;
    }

    /// <summary>
    /// Preferred names for the append-only trampoline list. Tried first; any other
    /// static <c>List&lt;object&gt;</c> on the patcher type is accepted so a rename
    /// in Il2CppInterop still fails closed.
    /// </summary>
    public static readonly string[] CacheFieldNames =
    {
        "DelegateCache",
        "delegateCache",
        "s_delegateCache",
    };

    /// <summary>
    /// Grep line when the patcher type has no static <c>List&lt;object&gt;</c>.
    /// Skip-wait is then disabled so a later cache hit is not silently lost.
    /// </summary>
    public static string FormatCacheMissing() =>
        "pin-diag HarmonyIL2CPP DelegateCache: no static List<object> field; skip-wait disabled";

    /// <summary>
    /// First static <c>List&lt;object&gt;</c> on <paramref name="patcherType"/>,
    /// preferring <see cref="CacheFieldNames"/>. Null when the type is missing or
    /// the field was renamed to something that is not a <c>List&lt;object&gt;</c>.
    /// </summary>
    public static FieldInfo? FindStaticListObjectField(Type? patcherType)
    {
        if (patcherType == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        for (int i = 0; i < CacheFieldNames.Length; i++)
        {
            var named = patcherType.GetField(CacheFieldNames[i], flags);
            if (IsStaticListObject(named)) return named;
        }
        FieldInfo[] fields;
        try { fields = patcherType.GetFields(flags); }
        catch { return null; }
        for (int i = 0; i < fields.Length; i++)
        {
            if (IsStaticListObject(fields[i])) return fields[i];
        }
        return null;
    }

    /// <summary>True when <paramref name="field"/> is a static <c>List&lt;object&gt;</c>.</summary>
    public static bool IsStaticListObject(FieldInfo? field)
    {
        if (field == null || !field.IsStatic) return false;
        var type = field.FieldType;
        if (type == typeof(List<object>)) return true;
        if (!type.IsGenericType) return false;
        if (type.GetGenericTypeDefinition() != typeof(List<>)) return false;
        var args = type.GetGenericArguments();
        return args.Length == 1 && args[0] == typeof(object);
    }

    /// <summary>
    /// A plugin whose methods are in the detour cache will never collect. Skip the
    /// bounded wait and latch immediately. When the cache field cannot be resolved,
    /// fail closed: wait the normal frames (treat plugins as possibly pinned).
    /// </summary>
    public static bool SkipPluginAlcWait(bool pinnedByDetourCache) =>
        SkipPluginAlcWait(pinnedByDetourCache, cacheAvailable: true);

    /// <summary>
    /// Same as <see cref="SkipPluginAlcWait(bool)"/>. <paramref name="cacheAvailable"/>
    /// false (renamed field / missing type) never skips the wait.
    /// </summary>
    public static bool SkipPluginAlcWait(bool pinnedByDetourCache, bool cacheAvailable) =>
        cacheAvailable && pinnedByDetourCache;

    /// <summary>Wait budget: 0 frames when the cache already proves a pin.</summary>
    public static int PluginAlcWaitFrames(bool pinnedByDetourCache) =>
        PluginAlcWaitFrames(pinnedByDetourCache, cacheAvailable: true);

    /// <summary>
    /// Wait budget. Unavailable cache = normal frames (fail closed), not a skip.
    /// </summary>
    public static int PluginAlcWaitFrames(bool pinnedByDetourCache, bool cacheAvailable) =>
        SkipPluginAlcWait(pinnedByDetourCache, cacheAvailable) ? 0 : CoreReloadPolicy.PluginAlcWaitFrames;

    public static string NamedAlc(string? name) =>
        string.IsNullOrEmpty(name) ? CoreReloadPolicy.UnnamedAlcName : name;

    public static string MethodLabel(in Entry entry)
    {
        if (entry.MethodAlcCollectible && !string.IsNullOrEmpty(entry.MethodName))
            return entry.MethodName;
        if (entry.TargetAlcCollectible && !string.IsNullOrEmpty(entry.TargetTypeName))
        {
            if (!string.IsNullOrEmpty(entry.MethodName))
                return entry.TargetTypeName + " " + entry.MethodName;
            return entry.TargetTypeName + ".Target";
        }
        if (!string.IsNullOrEmpty(entry.MethodName)) return entry.MethodName;
        if (!string.IsNullOrEmpty(entry.TargetTypeName)) return entry.TargetTypeName + ".Target";
        return "?";
    }

    /// <summary>One <c>DelegateCache</c> item, stripped of live <see cref="Delegate"/> refs for tests.</summary>
    public struct Entry
    {
        public string? MethodName;
        public string? MethodAssemblyName;
        public string? MethodAlcName;
        public bool MethodAlcCollectible;
        public string? TargetTypeName;
        public string? TargetAssemblyName;
        public string? TargetAlcName;
        public bool TargetAlcCollectible;
    }
}
