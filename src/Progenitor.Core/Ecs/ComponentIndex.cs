using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// Cached <see cref="TypeIndex"/> and <see cref="ComponentType"/> lookups that never call the
/// generic <c>TypeManager.GetTypeIndex&lt;T&gt;()</c> interop. That generic needs an IL2CPP
/// instantiation which, when missing, hard-crashes the process. <see cref="Il2CppType.Of{T}"/>
/// plus the non-generic <see cref="TypeManager.GetTypeIndex(Il2CppSystem.Type)"/> is the path
/// proven in the prefab spike.
/// </summary>
public static class ComponentIndex
{
    private static readonly Dictionary<Type, TypeIndex> ByManagedType = new();

    /// <summary>
    /// Called from Core Install so SkipCoreModules can log a skip. The type-index cache
    /// fills on first use; skip does not clear it.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.EcsModule))
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.EcsModule));
    }

    /// <summary>
    /// Cached TypeIndex entries live in this collectible ALC. Interop <c>Type</c> keys
    /// do not pin Core; the dictionary itself does until Unload.
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            lock (ByManagedType)
            {
                if (ByManagedType.Count > 0)
                    holds.Add("typeIndexCache=" + ByManagedType.Count);
            }
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.EcsModule, holds);
    }

    /// <summary>
    /// TypeIndex for <typeparamref name="T"/>, cached per closed T.
    /// Generic-instantiation-safe: this method is a C# generic only; the IL2CPP call is
    /// <c>TypeManager.GetTypeIndex(Il2CppType.Of&lt;T&gt;())</c>, which takes a class pointer
    /// rather than instantiating a native generic.
    /// </summary>
    public static TypeIndex IndexOf<T>() => IndexCache<T>.Value;

    /// <summary>
    /// TypeIndex for a managed (interop wrapper) type, cached per <see cref="Type"/>.
    /// Generic-instantiation-safe: uses <see cref="Il2CppType.From(Type)"/> and the non-generic
    /// <see cref="TypeManager.GetTypeIndex(Il2CppSystem.Type)"/>. The type must be one IL2CPP
    /// knows; a purely managed class that was never injected will throw rather than crash.
    /// </summary>
    public static TypeIndex IndexOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (ByManagedType)
        {
            if (ByManagedType.TryGetValue(type, out var cached))
                return cached;
            var index = TypeManager.GetTypeIndex(Il2CppType.From(type));
            ByManagedType[type] = index;
            return index;
        }
    }

    /// <summary>
    /// A <see cref="ComponentType"/> for <typeparamref name="T"/> with the given access mode.
    /// Generic-instantiation-safe: constructs via <c>new ComponentType(Il2CppType.Of&lt;T&gt;(), access)</c>,
    /// the same non-generic constructor the prefab spike used for queries. Does not call
    /// <c>ComponentType.ReadOnly&lt;T&gt;()</c> / <c>ReadWrite&lt;T&gt;()</c>.
    /// </summary>
    public static ComponentType Of<T>(ComponentType.AccessMode access)
        => new(Il2CppType.Of<T>(), access);

    private static class IndexCache<T>
    {
        // First access of IndexOf<T> runs this, which is when a world already exists and
        // TypeManager is initialized. C# generic static, not an IL2CPP generic method.
        internal static readonly TypeIndex Value = TypeManager.GetTypeIndex(Il2CppType.Of<T>());
    }
}
