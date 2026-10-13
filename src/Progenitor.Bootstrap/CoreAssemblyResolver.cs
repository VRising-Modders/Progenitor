using System;
using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Bootstrap;

/// <summary>
/// Core-ALC <c>Resolving</c> map. Bind by simple name, ignore version. Returns already-loaded
/// default-ALC assemblies (BepInEx, Harmony, Il2CppInterop, Unity, ProjectM, Bootstrap).
/// Never returns an assembly named <c>Progenitor</c>: that would let a default-ALC copy pin
/// the collectible Core. Plugin ALCs use <see cref="PluginAssemblyResolver"/> instead.
/// </summary>
public static class CoreAssemblyResolver
{
    public const string CoreSimpleName = "Progenitor";

    public static Assembly? Resolve(string? simpleName, IEnumerable<Assembly> defaultAssemblies)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;
        if (string.Equals(simpleName, CoreSimpleName, StringComparison.Ordinal))
            return null;
        if (defaultAssemblies == null) return null;
        foreach (var asm in defaultAssemblies)
        {
            if (asm == null) continue;
            string? name;
            try { name = asm.GetName().Name; }
            catch { continue; }
            if (string.Equals(name, simpleName, StringComparison.Ordinal))
                return asm;
        }
        return null;
    }
}
