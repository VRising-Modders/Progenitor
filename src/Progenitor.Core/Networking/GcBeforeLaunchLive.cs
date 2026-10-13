#if !PROGENITOR_TESTS
using System;
using BepInEx.Configuration;
using Progenitor.Config;
using ProjectM;

namespace Progenitor.Networking;

/// <summary>
/// Game half of <see cref="GcBeforeLaunch"/>: binds <c>[Diagnostics] CollectBeforeWorldLoad</c>
/// and fills Bootstrap's <c>GameHelper.LaunchGame</c> plugin slot with Core's dispatcher.
/// Client only. No Bootstrap change: Bootstrap already patches that original on every client
/// (the dedicated-join prefix) and dispatches to the slot after its own connect record.
/// </summary>
internal static class GcBeforeLaunchLive
{
    /// <summary>
    /// Same shape as Bootstrap's <c>HarmonySlots.LaunchGame</c>; Bootstrap rebinds it with
    /// <c>Delegate.CreateDelegate</c>. A named delegate, not <c>Action&lt;GameConnect&gt;</c>, so
    /// Core adds no generic instantiation over the game's value-type wrapper.
    /// </summary>
    private delegate void LaunchGameHandler(GameConnect launchData);

    private static ConfigEntry<bool>? _entry;
    private static string? _owner;

    internal static void Install()
    {
        if (Plugin.IsServerProcess) return;
        try
        {
            SharedConfig.BindWhenAvailable(
                "[" + GcBeforeLaunch.ConfigSection + "] " + GcBeforeLaunch.ConfigKey, BindFrom);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(GcBeforeLaunch.LogPrefix + "config bind threw (" + e.Message + "); using the default (on).");
        }
        Arm();
    }

    private static void BindFrom(ConfigFile file)
    {
        try
        {
            var entry = ConfigHelper.Bind(
                file,
                GcBeforeLaunch.ConfigSection,
                GcBeforeLaunch.ConfigKey,
                GcBeforeLaunch.DefaultEnabled,
                GcBeforeLaunch.ConfigDescription);
            _entry = entry;
            // Read at every launch, so an edited .cfg applies on the next join without a reload.
            GcBeforeLaunch.SetEnabledReader(() => entry.Value);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(GcBeforeLaunch.LogPrefix + "config bind failed (" + e.Message + "); using the default (on).");
        }
    }

    private static void Arm()
    {
        try
        {
            var dispatcher = new LaunchGameHandler(OnLaunchGame);
            string owner = HarmonySlotHandoff.InferOwner(dispatcher);
            if (string.IsNullOrEmpty(owner)) owner = "progenitor-core";
            if (HarmonySlotHandoff.TryBindPluginDirect(HarmonySlotHandoff.LaunchGame, dispatcher, owner))
            {
                _owner = owner;
                GcBeforeLaunch.SetSlotArmed(true);
                Plugin.Logger?.LogInfo(GcBeforeLaunch.ArmedLine);
                return;
            }
            Plugin.Logger?.LogWarning(GcBeforeLaunch.FallbackLine(
                "Bootstrap has no LaunchGame slot, or another owner bound it first"));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(GcBeforeLaunch.FallbackLine("bind threw: " + e.Message));
        }
    }

    /// <summary>
    /// Runs inside Bootstrap's <c>GameHelper.LaunchGame</c> prefix on the main thread. The
    /// chained plugin dispatcher (Dev's <c>host_capture</c> dump) runs first so the collection
    /// is the last managed work before the original starts the scene load. Never throws.
    /// </summary>
    private static void OnLaunchGame(GameConnect launchData)
    {
        var chained = GcBeforeLaunch.Chained;
        if (chained != null)
        {
            try
            {
                if (chained is LaunchGameHandler own) own(launchData);
                else if (chained is Action<GameConnect> action) action(launchData);
                else chained.DynamicInvoke(launchData);
            }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogWarning(GcBeforeLaunch.LogPrefix + "chained LaunchGame handler (" + GcBeforeLaunch.ChainedOwner + ") threw: " + e.Message); }
                catch { /* must not throw into LaunchGame */ }
            }
        }
        GcBeforeLaunch.OnSlotLaunch();
    }

    internal static void Uninstall()
    {
        string? owner = _owner;
        _owner = null;
        if (owner != null)
        {
            try { HarmonySlotHandoff.ClearPluginDirect(HarmonySlotHandoff.LaunchGame, owner); }
            catch (Exception e) { Plugin.Logger?.LogWarning(GcBeforeLaunch.LogPrefix + "slot clear: " + e.Message); }
        }
        GcBeforeLaunch.Reset();
        _entry = null;
    }
}
#endif
