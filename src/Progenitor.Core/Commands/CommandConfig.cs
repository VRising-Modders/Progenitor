using System;
using Progenitor.Config;

namespace Progenitor.Commands;

/// <summary>
/// Binds the <c>[Commands]</c> keys on the shared Progenitor cfg and pushes parsed
/// <see cref="CommandRoutingSettings"/> into <see cref="CommandRegistry"/>. Game-only
/// bind path is compiled out of the test assembly.
/// </summary>
internal static class CommandConfig
{
    internal const string Section = "Commands";
    internal const string FallbackRootKey = "FallbackRoot";
    internal const string AssignKey = "Assign";
    internal const string RenameKey = "Rename";
    internal const string DisableKey = "Disable";

#if !PROGENITOR_TESTS
    private static BepInEx.Configuration.ConfigEntry<string>? _fallbackRoot;
    private static BepInEx.Configuration.ConfigEntry<string>? _assign;
    private static BepInEx.Configuration.ConfigEntry<string>? _rename;
    private static BepInEx.Configuration.ConfigEntry<string>? _disable;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        // Defaults until the shared file binds; with a current Bootstrap that is right now.
        if (!SharedConfig.BindWhenAvailable("[" + Section + "]", BindFrom))
            CommandRegistry.TryApplySettings(CommandRoutingSettings.Default, out _);
    }

    private static void BindFrom(BepInEx.Configuration.ConfigFile file)
    {
        if (_installed) return;
        try
        {
            _fallbackRoot = ConfigHelper.Bind(
                file,
                Section,
                FallbackRootKey,
                CommandRoutingSettings.DefaultFallbackRoot,
                "Single token reserved for qualified command routes (default p). Example: .p example.mod ember");
            _assign = ConfigHelper.Bind(
                file,
                Section,
                AssignKey,
                "",
                "Assign a short token to one owner. One mapping per line or semicolon: token = ownerId");
            _rename = ConfigHelper.Bind(
                file,
                Section,
                RenameKey,
                "",
                "Rename a shortcut. One mapping per line or semicolon: ownerId:name = newToken or ownerId:group/name = newToken");
            _disable = ConfigHelper.Bind(
                file,
                Section,
                DisableKey,
                "",
                "Disable shortcuts for an owner or one command. One ownerId or ownerId:name (or ownerId:group/name) per line or semicolon.");

            ConfigHelper.AddSettingChanged(file, OnSettingChanged);
            ConfigHelper.Reloaded += OnReloaded;
            _installed = true;
            ApplyFromEntries("install");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Commands: config bind failed ({e.Message}); using default routing settings.");
            CommandRegistry.TryApplySettings(CommandRoutingSettings.Default, out _);
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        ConfigHelper.Reloaded -= OnReloaded;
        _fallbackRoot = null;
        _assign = null;
        _rename = null;
        _disable = null;
    }

    private static void OnReloaded(BepInEx.Configuration.ConfigFile file)
    {
        if (file == null || !ConfigHelper.IsSharedConfigFile(file)) return;
        ApplyFromEntries("reload");
    }

    private static void OnSettingChanged(object? sender, BepInEx.Configuration.SettingChangedEventArgs args)
    {
        var def = args?.ChangedSetting?.Definition;
        if (def == null) return;
        if (!string.Equals(def.Section, Section, StringComparison.Ordinal)) return;
        ApplyFromEntries("change");
    }

    private static void ApplyFromEntries(string reason)
    {
        string fallback = _fallbackRoot?.Value ?? CommandRoutingSettings.DefaultFallbackRoot;
        string assign = _assign?.Value ?? "";
        string rename = _rename?.Value ?? "";
        string disable = _disable?.Value ?? "";
        if (!CommandRoutingSettings.TryParse(fallback, assign, rename, disable, out var settings, out var parseError))
        {
            Plugin.Logger?.LogError(
                $"Commands: rejected {reason} of routing config ({parseError}). Previous routing table kept.");
            return;
        }

        if (!CommandRegistry.TryApplySettings(settings, out var applyError))
        {
            Plugin.Logger?.LogError(
                $"Commands: rejected {reason} of routing config ({applyError}). Previous routing table kept.");
        }
    }
#endif
}
