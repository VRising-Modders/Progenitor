using System;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Players;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// A server-wide item list (<c>ctx.Settings.ItemList</c>): one <see cref="ItemLimitList"/> for everyone, stored in the
/// mod's cfg as text, synced to clients that have the mod and published to every Progenitor client, where admins edit it
/// with the item picker. The server checks every edit against <see cref="Rules"/>. Main thread only.
/// </summary>
public sealed class ItemListSetting : IDisposable
{
    private readonly IDisposable _rules;
    private ItemLimitList _value = ItemLimitList.Empty;
    private string _parsed = "\0";
    private bool _warned;
    private bool _disposed;

    internal ItemListSetting(PluginContext ctx, string section, string key, string description, ItemListRules rules, ItemLimitList? defaultValue)
    {
        rules.Validate();
        string id = ScopedSettings.RowId(ctx.Guid, section, key);
        var fallback = defaultValue ?? ItemLimitList.Empty;
        if (fallback.Check(rules) is { } problem) throw new ArgumentException("The default list breaks its rules: " + problem, nameof(defaultValue));
        Rules = rules;
        // Registered first, so the published row is described as an item list and its edits are checked as one.
        _rules = ItemListSettings.Register(id, rules);
        try
        {
            Entry = ctx.Bind(section, key, fallback.ToString(), SettingScope.ServerWide, description);
        }
        catch
        {
            _rules.Dispose();
            throw;
        }
    }

    /// <summary>The cfg entry holding the list's text (the server's value on a joined client).</summary>
    public ConfigEntry<string> Entry { get; }

    public ItemListRules Rules { get; }

    /// <summary>
    /// The list now (parsed once per change of the stored text, so reading it every frame is cheap). Stored text that
    /// does not parse or keep the rules reads as empty (logged once). To react to changes, subscribe to the cfg file's
    /// change notification as for any other entry (<c>Entry.ConfigFile.SettingChanged</c>, removed on unload).
    /// </summary>
    public ItemLimitList Value
    {
        get
        {
            string text = Entry.Value ?? "";
            if (text == _parsed) return _value;
            _parsed = text;
            if (ItemLimitList.TryParse(text, Rules, out var list, out var error)) { _value = list; _warned = false; }
            else
            {
                _value = ItemLimitList.Empty;
                if (!_warned) Plugin.Logger?.LogWarning("Item list " + Entry.Definition.Key + " holds a value it does not accept (" + error + "); it reads as empty.");
                _warned = true;
            }
            return _value;
        }
    }

    /// <summary>Server side: replace the list. Refused (returns the reason) when it breaks <see cref="Rules"/>.</summary>
    public string? Set(ItemLimitList list)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ItemListSetting));
        if (list == null) throw new ArgumentNullException(nameof(list));
        if (list.Check(Rules) is { } problem) return problem;
        Entry.Value = list.ToString();
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rules.Dispose();
    }
}

/// <summary>
/// An item list the server keeps for each player (<c>ctx.Settings.PerPlayerItemList</c>), stored in the players store
/// like other <c>PerPlayer</c> settings. Players edit their own with the item picker under <b>Your settings on this
/// server</b>; the server checks every edit against <see cref="Rules"/>. Main thread only.
/// </summary>
public sealed class PlayerItemListSetting : IDisposable
{
    private readonly IDisposable _rules;
    private bool _disposed;

    internal PlayerItemListSetting(PluginContext ctx, string section, string key, string description, ItemListRules rules, ItemLimitList? defaultValue)
    {
        rules.Validate();
        Rules = rules;
        var fallback = defaultValue ?? ItemLimitList.Empty;
        if (fallback.Check(rules) is { } problem) throw new ArgumentException("The default list breaks its rules: " + problem, nameof(defaultValue));
        _rules = ItemListSettings.Register(ScopedSettings.RowId(ctx.Guid, section, key), rules);
        try
        {
            Setting = new PlayerSetting<string>(ctx, section, key, fallback.ToString(), description, null,
                text => ItemLimitList.TryParse(text, rules, out _, out var error) ? null : error);
        }
        catch
        {
            _rules.Dispose();
            throw;
        }
        Setting.Changed += OnChanged;
    }

    /// <summary>The underlying text setting (place it on the page with <c>page.ItemList(this)</c>).</summary>
    internal PlayerSetting<string> Setting { get; }

    public ItemListRules Rules { get; }

    public string Section => Setting.Section;

    public string Key => Setting.Key;

    /// <summary>Server: this account's list.</summary>
    public ItemLimitList For(PlayerAccountId player) => Parse(Setting.For(player));

    /// <summary>Server: the list of the account behind a User entity.</summary>
    public ItemLimitList For(Entity userEntity) => Parse(Setting.For(userEntity));

    /// <summary>Client: whether the joined server has sent this player's list.</summary>
    public bool HasMine => Setting.HasMine;

    /// <summary>Client: this player's list on the joined server (throws until it has been sent).</summary>
    public ItemLimitList Mine => Parse(Setting.Mine);

    /// <summary>Client: ask the server to store <paramref name="list"/> as this player's.</summary>
    public IDisposable Set(ItemLimitList list, Action<ServerSettingResult> completed)
    {
        if (list == null) throw new ArgumentNullException(nameof(list));
        return Setting.Set(list.ToString(), completed);
    }

    /// <summary>Server: store <paramref name="list"/> for an account (same checks as a player's edit).</summary>
    public ServerSettingResult SetFor(PlayerAccountId player, ItemLimitList list)
    {
        if (list == null) throw new ArgumentNullException(nameof(list));
        return Setting.SetFor(player, list.ToString());
    }

    /// <summary>Fires on the game thread when an account's list changes (client: the local player, <c>PlayerAccountId.None</c>).</summary>
    public event Action<PlayerAccountId, ItemLimitList>? Changed;

    private void OnChanged(PlayerAccountId player, string text)
    {
        try { Changed?.Invoke(player, Parse(text)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Item list subscriber: " + e.Message); }
    }

    private ItemLimitList Parse(string text) => ItemLimitList.TryParse(text, Rules, out var list, out _) ? list : ItemLimitList.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Setting.Changed -= OnChanged;
        Changed = null;
        Setting.Dispose();
        _rules.Dispose();
    }
}
