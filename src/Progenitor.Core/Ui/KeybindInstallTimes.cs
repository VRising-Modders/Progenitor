#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// KEYS1: a mod's install time for the "first installed" rule (<see cref="KeybindArbiter"/>).
///
/// <para><b>Precedence</b> is the Install tab's Installed column's, through the shared
/// <c>Progenitor.Install.InstallTimes.Of(pluginId, filePath, gameRoot)</c>: the install ledger, then the
/// plugin file's creation time, then its last-write time. This class finds the file — the chainloader's,
/// or a hot-loaded plugin's through the reloader — and caches the answer.</para>
/// </summary>
internal static class KeybindInstallTimes
{
    private const int CacheMs = 10000;
    private static readonly Dictionary<string, (ModInstallTime Time, long At)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The install time of <paramref name="pluginId"/> on this client, cached.</summary>
    internal static ModInstallTime Of(string pluginId)
    {
        string id = (pluginId ?? "").Trim();
        if (id.Length == 0) return ModInstallTime.Unknown;
        long now = Environment.TickCount64;
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var hit) && now - hit.At < CacheMs) return hit.Time;
        }

        string? gameRoot = null;
        try { gameRoot = ServerInstall.GameRootOf(Paths.BepInExRootPath); }
        catch { gameRoot = null; }
        var time = Lookup(id, PluginFileOf(id), gameRoot);
        lock (Cache) Cache[id] = (time, now);
        return time;
    }

    internal static void Forget()
    {
        lock (Cache) Cache.Clear();
    }

    /// <summary>
    /// The shared precedence (<see cref="InstallTimes.Of(string?, string?, string?)"/>, the Install tab's
    /// Installed column): the install ledger, then the file's creation time, then its last-write time.
    /// This class only finds the file (hot-loaded plugins included) and caches the answer.
    /// </summary>
    internal static ModInstallTime Lookup(string pluginId, string? filePath, string? gameRoot)
    {
        var t = InstallTimes.Of(pluginId, filePath, gameRoot);
        if (!t.Known) return ModInstallTime.Unknown;
        return new ModInstallTime(t.Utc, t.Source == InstallTimeSource.Ledger ? KeybindArbiter.InstallLedger : KeybindArbiter.InstallFile);
    }

    /// <summary>
    /// The DLL <paramref name="id"/> was loaded from, or null. Review 2026-09-29 (blocking): hot-loaded
    /// plugins in <c>progenitor-reload/</c> are not in the chainloader's list, so they are found through the
    /// reloader: first through the owner of their live keybinds (the plugin instance), then by scanning the
    /// reloader-owned assemblies for a <c>[BepInPlugin]</c> with that GUID (as the Mods list does).
    /// </summary>
    private static string? PluginFileOf(string id) =>
        KeybindPluginFiles.First(id, FromChainloader, FromKeybindOwner, FromReloadAssemblies);

    private static string? FromChainloader(string id)
    {
        try
        {
            var loader = IL2CPPChainloader.Instance;
            if (loader?.Plugins == null || !loader.Plugins.TryGetValue(id, out var info) || info == null) return null;
            var instance = info.Instance;
            if (instance != null && Reload.PluginReloader.TryGetSourcePath(instance.GetType().Assembly, out var reloadPath))
                return reloadPath;
            return string.IsNullOrEmpty(info.Location) ? null : info.Location;
        }
        catch { return null; }
    }

    private static string? FromKeybindOwner(string id)
    {
        try
        {
            var owner = Keybinds.OwnerOf(id);
            if (owner == null || owner is Type) return null;
            return Reload.PluginReloader.TryGetSourcePath(owner.GetType().Assembly, out var path) ? path : null;
        }
        catch { return null; }
    }

    private static string? FromReloadAssemblies(string id)
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!Reload.PluginReloader.TryGetSourcePath(assembly, out var path)) continue;
                Type?[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    var meta = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<BepInPlugin>(type, inherit: false);
                    if (meta != null && string.Equals(meta.GUID, id, StringComparison.OrdinalIgnoreCase)) return path;
                }
            }
        }
        catch { /* unknown */ }
        return null;
    }
}
#endif
