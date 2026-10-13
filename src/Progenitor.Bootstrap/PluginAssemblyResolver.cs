using System;
using System.Reflection;

namespace Progenitor.Bootstrap;

/// <summary>
/// Plugin-ALC <c>Resolving</c> map. Bind by simple name, ignore version. Returns the
/// Core generation the plugin ALC was loaded against (pinned on the <c>Loaded</c> record,
/// not <c>CoreLoader.Assembly</c>) and Bootstrap (already in the default ALC). An
/// uncollected old plugin ALC that resolved "current" Core would mix gen N types with
/// gen N+1. Returning null lets the default context resolve BepInEx / interop. Do not
/// register this on the default ALC: a chainloader plugin that referenced Core would
/// then pin it.
/// </summary>
internal static class PluginAssemblyResolver
{
    public const string CoreSimpleName = "Progenitor";
    public const string BootstrapSimpleName = "Progenitor.Bootstrap";

    public static Assembly? Resolve(string? simpleName, Assembly? core, Assembly? bootstrap)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;
        if (string.Equals(simpleName, CoreSimpleName, StringComparison.Ordinal))
            return core;
        if (string.Equals(simpleName, BootstrapSimpleName, StringComparison.Ordinal))
            return bootstrap;
        return null;
    }
}
