#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Progenitor.Dye;

/// <summary>
/// DYEALL in Core (decided 2026-10-08: "part of Core Progenitor, one of the small quality-of-life
/// features"). In Character &gt; Equipment, hold a color swatch until the ring fills and every worn piece and
/// transmog takes that color; a click keeps the game's behavior. Client only and on by default; there is no
/// plugin API. It sends the same <c>ChangeTransmogColorEvent</c> the game's picker sends, so the server needs
/// nothing (a server without Progenitor works the same).
///
/// <para><b>Settings.</b> <c>[DyeAll]</c> in <c>progenitor.ui.cfg</c> (<see cref="DyeAllEntries"/>); Progenitor's
/// page draws <b>Dye all on hold</b> under <b>Equipment</b>. A hold is always one second, and a piece under a transmog is always dyed. The former mod's
/// <c>progenitor.dyeall.cfg</c> is read once on the first install (<see cref="DyeAllLegacy.ImportOnce"/>) and
/// never written. There is no dye key: the hold is a pointer press on a swatch.</para>
///
/// <para><b>The former mod.</b> While <c>progenitor.dyeall</c> still runs, this copy stands down
/// (<see cref="DyeAllClient"/>); that mod's page says it can be uninstalled (<see cref="DyeAllLegacy.NoteFor"/>).</para>
///
/// <para><b>Lifetime.</b> Installed and uninstalled with Core (a Core swap or process exit). Uninstall stops the
/// client (frame hook, swatch watches, ring, line); the cfg values stay.</para>
/// </summary>
internal static class DyeAllFeature
{
    private sealed class CoreOwner
    {
        public override string ToString() => "Progenitor dye all";
    }

    /// <summary>Owner of the swatch watches.</summary>
    internal static readonly object Owner = new CoreOwner();

    private static DyeAllClient? _client;
    private static bool _installed;

    internal static void Install()
    {
        if (Plugin.IsServerProcess || _installed) return;
        var log = Plugin.Logger;
        var file = UiKeybindHost.CoreConfig;
        if (file == null || log == null)
        {
            log?.LogWarning("Dye all: Core's ui cfg is not open; hold-to-dye is off for this generation.");
            return;
        }

        _installed = true;
        try
        {
            var entries = DyeAllEntries.Bind(new CoreBinder(file));
            string? imported = DyeAllLegacy.ImportOnce(Paths.ConfigPath, entries);
            if (imported != null) log.LogInfo(imported);

            var client = new DyeAllClient(log, Owner, entries, OldModRunning);
            _client = client;
            client.Attach();
        }
        catch (Exception e)
        {
            log.LogError("Dye all: install threw; hold-to-dye is off for this generation: " + e);
            Uninstall();
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { _client?.Stop(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dye all: stop threw: " + e.Message); }
        _client = null;
    }

    /// <summary>The dev bridge's <c>dye</c> readback (<see cref="DyeAllTestBridge"/>).</summary>
    internal static Dictionary<string, object?> Describe(bool cells)
    {
        var client = _client;
        if (client == null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["installed"] = _installed,
                ["client"] = false,
                ["server"] = Plugin.IsServerProcess,
            };
        }

        var r = client.Describe(cells);
        r["installed"] = _installed;
        r["client"] = true;
        return r;
    }

    /// <summary>Whether the former Dye all mod runs in this process (<see cref="DyeAllLegacy.OldModRunning"/>).</summary>
    private static bool OldModRunning()
    {
        if (ModRegistry.TryGet(DyeAllLegacy.PluginGuid, out var entry))
            return DyeAllLegacy.OldModRunning(true, entry.Running, false);
        bool chainloaded = false;
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            chainloaded = plugins != null && plugins.ContainsKey(DyeAllLegacy.PluginGuid);
        }
        catch { chainloaded = false; }
        return DyeAllLegacy.OldModRunning(false, false, chainloaded);
    }

    /// <summary><see cref="IDyeAllBinder"/> over Core's ui cfg.</summary>
    private sealed class CoreBinder : IDyeAllBinder
    {
        private readonly ConfigFile _file;

        internal CoreBinder(ConfigFile file) => _file = file;

        public ConfigEntry<bool> Flag(string section, string key, bool value, string description) =>
            _file.Bind(section, key, value, description);
    }
}

/// <summary>
/// The dev bridge's <c>dye</c> op (<c>Progenitor.Dev.Ops.DyeOps</c>) reads Dye all through this by reflection (Core
/// grants Dev no <c>InternalsVisibleTo</c>); it is not a plugin API and no network path reaches it. Main thread.
/// </summary>
internal static class DyeAllTestBridge
{
    /// <summary>The scan step, menu, world, containers, listed and watched cells, hint and last hold; <paramref name="cells"/> adds the shown cells' screen centers.</summary>
    public static Dictionary<string, object?> Status(bool cells) => DyeAllFeature.Describe(cells);
}
#endif
