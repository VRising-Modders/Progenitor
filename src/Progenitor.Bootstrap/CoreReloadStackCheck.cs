using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Progenitor.Bootstrap;

/// <summary>
/// Pin-diag stack walk at collect start. A collectible ALC cannot unload while any
/// frame on any thread still references its assemblies; the live failure was
/// <c>RequestReload</c> running inside the Dev bridge op so Progenitor.Dev and Core
/// frames sat on the main thread through <c>ALC.Unload</c>. This walk logs whether
/// that is still true. Uses <see cref="StackTrace"/> (the frame form of
/// <c>Environment.StackTrace</c>) and each frame's <c>DeclaringType.Assembly</c>.
/// </summary>
public static class CoreReloadStackCheck
{
    /// <summary>
    /// Walk the current thread's stack and log <see cref="CoreReloadPolicy.FormatStackCheck"/>.
    /// Dirty frames (plugin ALC or the Core generation) are listed so the log proves
    /// the stack is clean — or names the pin.
    /// </summary>
    public static void Log(Assembly? coreAssembly, string? coreAlcName, Action<string> log)
    {
        if (log == null) throw new ArgumentNullException(nameof(log));
        int frames = 0;
        int plugin = 0;
        int core = 0;
        var dirty = new List<string>();
        try
        {
            var trace = new StackTrace(skipFrames: 0, fNeedFileInfo: false);
            var list = trace.GetFrames();
            if (list != null)
            {
                for (int i = 0; i < list.Length; i++)
                {
                    var method = list[i]?.GetMethod();
                    if (method == null) continue;
                    frames++;
                    Classify(method, coreAssembly, coreAlcName, ref plugin, ref core, dirty);
                }
            }
        }
        catch (Exception e)
        {
            log("pin-diag stack: " + e.Message);
            return;
        }

        log(CoreReloadPolicy.FormatStackCheck(frames, plugin, core));
        int shown = dirty.Count < 16 ? dirty.Count : 16;
        for (int i = 0; i < shown; i++)
            log("  stack " + dirty[i]);
        if (dirty.Count > shown)
            log("  ... " + (dirty.Count - shown) + " more");
    }

    /// <summary>True when <paramref name="alcName"/> is a reloadable plugin context.</summary>
    public static bool IsPluginAlc(string? alcName) => CoreReloadPolicy.IsPluginAlcName(alcName);

    internal static void Classify(
        MethodBase method,
        Assembly? coreAssembly,
        string? coreAlcName,
        ref int plugin,
        ref int core,
        List<string> dirty)
    {
        Type? type;
        Assembly? asm;
        try
        {
            type = method.DeclaringType;
            asm = type?.Assembly;
        }
        catch
        {
            return;
        }
        if (asm == null) return;

        string? alcName = null;
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(asm);
            alcName = alc?.Name;
        }
        catch
        {
            alcName = null;
        }

        bool isCore = false;
        try
        {
            if (coreAssembly != null && ReferenceEquals(asm, coreAssembly))
                isCore = true;
            else if (CoreReloadPolicy.IsCoreAlcName(alcName))
                isCore = true;
            else if (!string.IsNullOrEmpty(coreAlcName)
                     && string.Equals(alcName, coreAlcName, StringComparison.Ordinal))
                isCore = true;
        }
        catch
        {
            isCore = false;
        }

        bool isPlugin = !isCore && IsPluginAlc(alcName);
        if (!isCore && !isPlugin) return;

        string name = (type?.FullName ?? "?") + "." + (method.Name ?? "?");
        if (isCore)
        {
            core++;
            dirty.Add("core " + name + " alc=" + (alcName ?? "?"));
        }
        else
        {
            plugin++;
            dirty.Add("plugin " + name + " alc=" + (alcName ?? "?"));
        }
    }
}
