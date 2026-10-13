using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Plugins;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>Tracked menu contributions and explicit server-edit permissions for this plugin.</summary>
public sealed class PluginMenu
{
    private readonly PluginContext _ctx;
    private bool _serverLogged;
    internal PluginMenu(PluginContext ctx)
    {
        _ctx = ctx;
        SettingsCatalog.Register(ctx.Guid, ctx.Config.File, ctx.Owner);
    }

    public ModMenuRegistration Register(string name, string description = "")
    {
        var menu = ModsMenu.Register(_ctx.Owner, _ctx.Guid, name, description);
        _ctx.Track(menu);
        return menu;
    }

    /// <summary>
    /// Open the Mods menu at this plugin's custom page <paramref name="title"/> (one registered with
    /// <see cref="ModMenuRegistration.Page"/>). In a world the menu opens through the game's own menu, on its MODS tab,
    /// so the page shows on a later frame; at the title screen, or with the menu already open, it shows at once. Use
    /// this from a keybind or a button rather than <see cref="ModsMenu.OpenPage"/>, which draws the page without
    /// opening the game's menu and leaves an in-world player on its spellbook page. False, opening nothing, when this
    /// plugin has no page with that title, and on the server (client only, logged once).
    /// </summary>
    public bool OpenPage(string title)
    {
        if (_ctx.IsServer)
        {
            if (!_serverLogged)
            {
                _serverLogged = true;
                _ctx.Log.LogInfo("Ui: ctx.Menu.OpenPage is client only; on the server it opens nothing.");
            }

            return false;
        }

        if (ModsMenu.RequestModPage(_ctx.Guid, title)) return true;
        _ctx.Log.LogWarning("Ui: ctx.Menu.OpenPage: no page titled '" + title + "' is registered; register it with ctx.Menu.Register(...).Page first.");
        return false;
    }

    /// <summary>Opt a synced entry into server-validated admin editing. Call on both sides.</summary>
    public void AllowServerEdits(ConfigEntryBase entry) => _ctx.Track(ServerSettings.Register(_ctx.Guid, entry));

    /// <summary>
    /// Wrap an existing config entry as a settings binding. The entry remains the source of truth.
    /// Tracked: unload detaches callbacks and rejects in-flight completions.
    /// </summary>
    public SettingBinding RegisterSetting(ConfigEntryBase entry, SettingPresentation? presentation = null)
    {
        var binding = SettingsCatalog.RegisterSetting(_ctx.Guid, entry, presentation, _ctx.Owner);
        _ctx.Track(binding);
        return binding;
    }

    /// <summary>
    /// Register a typed action for the settings UI. Independently registered commands are not
    /// discovered; pass the same handler a command already uses when both surfaces should exist.
    /// </summary>
    public IDisposable RegisterAction(
        string id,
        Func<IReadOnlyList<string>, SettingActionResult> handler,
        SettingActionSpec? spec = null)
    {
        var action = SettingsCatalog.RegisterAction(_ctx.Guid, id, handler, spec, _ctx.Owner);
        _ctx.Track(action);
        return action;
    }
}
