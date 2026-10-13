using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;

namespace Progenitor.Reload;

/// <summary>
/// Drops <see cref="System.Text.Json"/>'s per-type caches for collectible types, so a plugin or
/// Core generation that serialized one of its own types can still unload.
///
/// <para><b>The pin (live dump, 2026-09-25).</b> A <see cref="JsonSerializerOptions"/> caches a
/// converter and a type-info object per <see cref="Type"/> it has serialized, and a
/// <see cref="Type"/> keeps its assembly's <c>LoaderAllocator</c> alive. The process-wide default
/// options — what <c>JsonSerializer.Serialize(value)</c> uses when no options are passed — live in
/// the non-collectible System.Text.Json assembly, so one call from a plugin, even on an anonymous
/// type (<c>new { reason }</c>), pins that plugin's load context for the life of the process, and
/// through the plugin's references pins the Core generation too. A server Core swap was refused
/// with <c>generation 1 still alive</c> for exactly this reason, and no reflective holder scan could
/// see it. Core's own <see cref="Persistence.ModStore.JsonOptions"/> is the same shape with a
/// shorter lifetime: it held every plugin state type it had stored until Core itself unloaded.</para>
///
/// <para><b>What this does.</b> Removes every cache entry whose key <see cref="Type.IsCollectible"/>
/// — which covers a closed generic over a collectible argument such as <c>List&lt;T&gt;</c> — and
/// the options' one-entry last-type cache when it names one. Entries for live generations go too;
/// they are rebuilt on the next call, so the only cost is one reflection pass per type afterwards.
/// Non-collectible entries are kept.</para>
///
/// <para><b>Layout.</b> The caches are private: <c>_converters</c>, <c>_classes</c> and
/// <c>&lt;_lastClass&gt;k__BackingField</c> on the options, <c>s_defaultOptions</c> on the type, in
/// the System.Text.Json 6.0 the game's .NET 6.0.7 runtime ships. The game runtime is fixed, so the
/// sweep reads exactly that shape and reports anything else as unsupported rather than guessing at
/// another runtime's layout; a missing field is a no-op, never a throw.</para>
/// </summary>
internal static class JsonCacheSweep
{
    internal const string ConvertersField = "_converters";
    internal const string ClassesField = "_classes";
    internal const string LastClassField = "<_lastClass>k__BackingField";
    internal const string DefaultOptionsField = "s_defaultOptions";

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>
    /// Sweep the process-wide default options and Core's <see cref="Persistence.ModStore.JsonOptions"/>.
    /// Returns the number of entries removed; <paramref name="unsupported"/> names the first cache
    /// this runtime does not lay out as expected (null when every one was read).
    /// </summary>
    internal static int ReleaseProcessCaches(out string? unsupported)
    {
        unsupported = null;
        int removed = 0;
        foreach (var (name, options) in ProcessCaches())
        {
            removed += ReleaseCollectible(options, out var missing);
            if (missing != null && unsupported == null)
                unsupported = name + "." + missing;
        }
        return removed;
    }

    /// <summary>
    /// <c>pin-diag Json …</c> line: how many collectible types each process cache still holds, with
    /// a few type names. Read before a Core unload, it names this pin instead of leaving it to a dump.
    /// <c>default</c> outlives Core, so an entry there pins; <c>modStore</c> is a Core static and
    /// dies with Core, so its entries pin retired plugin generations only, never Core.
    /// </summary>
    internal static string DescribeProcessCaches()
    {
        var parts = new List<string>();
        var samples = new List<string>();
        foreach (var (name, options) in ProcessCaches())
        {
            int n = CountCollectible(options, samples, maxSamples: 6, out var missing);
            parts.Add(name + "=" + (missing != null ? "unsupported(" + missing + ")" : n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return "pin-diag Json collectible " + string.Join(" ", parts)
            + (samples.Count == 0 ? "" : " samples=[" + string.Join(",", samples) + "]");
    }

    private static IEnumerable<(string Name, object? Options)> ProcessCaches()
    {
        yield return ("default", DefaultOptions());
        yield return ("modStore", Persistence.ModStore.JsonOptions);
    }

    /// <summary>System.Text.Json's process-wide default options, or null when this runtime has no such field.</summary>
    internal static object? DefaultOptions()
    {
        try
        {
            return typeof(JsonSerializerOptions)
                .GetField(DefaultOptionsField, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Remove every cached entry keyed by a collectible type from <paramref name="options"/>.
    /// <paramref name="options"/> is typed <see cref="object"/> so a test can hand in any object
    /// with the same private shape. <paramref name="missing"/> names the first expected field that
    /// was absent or of the wrong shape.
    /// </summary>
    internal static int ReleaseCollectible(object? options, out string? missing)
    {
        missing = null;
        if (options == null)
        {
            missing = DefaultOptionsField;
            return 0;
        }
        int removed = 0;
        removed += RemoveCollectibleKeys(options, ConvertersField, ref missing);
        removed += RemoveCollectibleKeys(options, ClassesField, ref missing);

        var last = FindField(options.GetType(), LastClassField);
        if (last == null)
        {
            missing ??= LastClassField;
        }
        else
        {
            try
            {
                if (IsCollectibleTypeInfo(last.GetValue(options)))
                {
                    last.SetValue(options, null);
                    removed++;
                }
            }
            catch
            {
                missing ??= LastClassField;
            }
        }
        return removed;
    }

    /// <summary>Count (without removing) cached entries keyed by a collectible type.</summary>
    internal static int CountCollectible(object? options, List<string>? samples, int maxSamples, out string? missing)
    {
        missing = null;
        if (options == null)
        {
            missing = DefaultOptionsField;
            return 0;
        }
        int count = 0;
        foreach (var field in new[] { ConvertersField, ClassesField })
        {
            if (ReadDictionary(options, field) is not IDictionary dict)
            {
                missing ??= field;
                continue;
            }
            foreach (var key in SnapshotKeys(dict))
            {
                if (key is not Type type || !IsCollectible(type)) continue;
                count++;
                if (samples != null && samples.Count < maxSamples)
                {
                    string name = SafeName(type);
                    if (!samples.Contains(name)) samples.Add(name);
                }
            }
        }
        return count;
    }

    private static int RemoveCollectibleKeys(object options, string fieldName, ref string? missing)
    {
        if (ReadDictionary(options, fieldName) is not IDictionary dict)
        {
            missing ??= fieldName;
            return 0;
        }
        int removed = 0;
        foreach (var key in SnapshotKeys(dict))
        {
            if (key is not Type type || !IsCollectible(type)) continue;
            try
            {
                // ConcurrentDictionary's IDictionary.Remove is its TryRemove; a key another thread
                // already took out is simply not counted.
                int before = dict.Count;
                dict.Remove(key);
                if (dict.Count < before) removed++;
            }
            catch
            {
                // A concurrent writer may race the removal; the next sweep takes it.
            }
        }
        return removed;
    }

    private static object? ReadDictionary(object options, string fieldName)
    {
        try { return FindField(options.GetType(), fieldName)?.GetValue(options); }
        catch { return null; }
    }

    private static List<object> SnapshotKeys(IDictionary dict)
    {
        var keys = new List<object>();
        try
        {
            // ConcurrentDictionary.Keys is a point-in-time copy, so removing while walking it is safe.
            foreach (var key in dict.Keys)
                if (key != null) keys.Add(key);
        }
        catch
        {
            // A concurrent resize can throw on some dictionaries; return what was read.
        }
        return keys;
    }

    /// <summary>
    /// A JsonTypeInfo names the type it describes in its <c>Type</c> property (internal in 6.0,
    /// hence the non-public binding). Collectible when that type is.
    /// </summary>
    private static bool IsCollectibleTypeInfo(object? typeInfo)
    {
        if (typeInfo == null) return false;
        try
        {
            var prop = typeInfo.GetType().GetProperty("Type", Instance);
            return prop?.GetValue(typeInfo) is Type type && IsCollectible(type);
        }
        catch
        {
            // Unreadable: drop it. The field is a one-entry cache and is rebuilt on the next call.
            return true;
        }
    }

    private static bool IsCollectible(Type type)
    {
        try { return type.IsCollectible; }
        catch { return false; }
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }

    /// <summary>
    /// <c>assembly:Type</c>, naming the assembly that makes the entry collectible — for a closed
    /// generic such as <c>List&lt;T&gt;</c> that is <c>T</c>'s, not CoreLib's — so the line names
    /// the plugin to fix. Anonymous types have no useful full name.
    /// </summary>
    internal static string SafeName(Type type)
    {
        try
        {
            var owner = CollectibleOwner(type) ?? type;
            return (owner.Assembly.GetName().Name ?? "?") + ":" + type.Name;
        }
        catch
        {
            return "?";
        }
    }

    private static Type? CollectibleOwner(Type type)
    {
        if (type.Assembly.IsCollectible) return type;
        if (type.HasElementType && type.GetElementType() is { } element) return CollectibleOwner(element);
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                if (CollectibleOwner(arg) is { } found) return found;
        }
        return null;
    }
}
