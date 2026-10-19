using System;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// <c>ctx.Settings</c> (SETTINGS1 stage 2, docs/mod-settings.md): the layout of this plugin's
/// settings on its page in the Mods workspace. A thin, tracked wrapper over the settings page
/// registry: it supplies the plugin's owner and GUID, registers the plugin's config file as a
/// settings source (as <c>ctx.Menu</c> does), and tracks the page so every unload reason drops it.
///
/// <para>Hot-reload safe: the page holds the plugin's delegates (show-if / enable-if predicates,
/// button actions, info functions) in Core only; nothing is handed to Bootstrap or IL2CPP, and the
/// tracked registration drops them — and closes the page if it is on show — when the plugin
/// unloads.</para>
/// </summary>
public sealed class PluginSettings
{
    private readonly PluginContext _ctx;

    internal PluginSettings(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        try { SettingsCatalog.Register(ctx.Guid, ctx.Config.File, ctx.Owner); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: could not register '" + ctx.Guid + "' as a settings source: " + e.Message);
        }
    }

    /// <summary>
    /// Lay out this plugin's settings page. <paramref name="build"/> runs once, now; a builder that
    /// throws registers nothing and the exception reaches <c>OnLoad</c>. Once per plugin: a second
    /// page while the first is registered throws <see cref="ArgumentException"/>. An entry the page
    /// places appears only there — not again in a <c>.Settings(title, …)</c> group or in the automatic
    /// General / Keys sections. The returned handle is already tracked; dispose it only to drop the
    /// page early.
    /// </summary>
    public IDisposable Page(Action<SettingsPageBuilder> build)
    {
        var registration = ModSettingsPages.Register(_ctx.Owner, _ctx.Guid, build);
        _ctx.Track(registration);
        return registration;
    }

    /// <summary>Publish a personal preference whose value the server keeps separately for each player.</summary>
    public PlayerSetting<T> PerPlayer<T>(string section, string key, T defaultValue, string description = "",
        BepInEx.Configuration.AcceptableValueBase? acceptableValues = null, Func<T, string?>? validate = null)
    {
        var setting = new PlayerSetting<T>(_ctx, section, key, defaultValue, description, acceptableValues, validate);
        _ctx.Track(setting);
        return setting;
    }

    /// <summary>
    /// A server-wide item list: lines of an item (or, with <see cref="ItemListRules.AllowCategories"/>, a category) and a
    /// count, one list for everyone. Bind it on both sides like a <c>SettingScope.ServerWide</c> entry; admins edit it in
    /// the Mods menu with the item picker, also on clients without the mod. Stored in the cfg as text
    /// (<see cref="ItemLimitList"/>). <paramref name="rules"/> null: items only, at most 256 lines, counts 0 to 100,000.
    /// </summary>
    public ItemListSetting ItemList(string section, string key, string description = "", ItemListRules? rules = null,
        ItemLimitList? defaultValue = null)
    {
        var setting = new ItemListSetting(_ctx, section, key, description ?? "", rules ?? new ItemListRules(), defaultValue);
        _ctx.Track(setting);
        return setting;
    }

    /// <summary>
    /// An item list the server keeps for each player (as <see cref="PerPlayer{T}"/>): each player edits their own in the
    /// Mods menu with the item picker, also without the mod's DLL. Stored in the players store as text.
    /// </summary>
    public PlayerItemListSetting PerPlayerItemList(string section, string key, string description = "", ItemListRules? rules = null,
        ItemLimitList? defaultValue = null)
    {
        var setting = new PlayerItemListSetting(_ctx, section, key, description ?? "", rules ?? new ItemListRules(), defaultValue);
        _ctx.Track(setting);
        return setting;
    }
}
