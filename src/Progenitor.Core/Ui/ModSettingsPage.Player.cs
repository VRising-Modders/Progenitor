using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Settings page rows for server-kept settings (<c>ctx.Settings.PerPlayer</c>, item lists). A row placed here is drawn
/// from the joined server's definition, where the page puts it, under <b>Your settings on this server</b> (or
/// <b>Server settings</b> for a server-wide item list); with no server providing it, the row is not drawn. On the server
/// the same rows give the setting its label, help, control, section and order for clients without the mod's DLL.
/// </summary>
public sealed partial class SettingsPageBuilder
{
    /// <summary>A switch for a per-player <see cref="bool"/> setting.</summary>
    public SettingsPageBuilder Toggle(PlayerSetting<bool> setting, string? label = null, string? help = null,
        Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), SettingControl.Toggle, null, label, help, showIf, enableIf);

    /// <summary>A number field for a per-player number setting (<paramref name="units"/> follows the value).</summary>
    public SettingsPageBuilder Number<T>(PlayerSetting<T> setting, double? step = null, string? units = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), SettingControl.Number, NumberHints(false, null, null, step, units), label, help, showIf, enableIf);

    /// <summary>A slider for a per-player number setting with an <c>AcceptableValueRange</c>; without one it is a number field.</summary>
    public SettingsPageBuilder Slider<T>(PlayerSetting<T> setting, double? step = null, string? units = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), SettingControl.Slider, NumberHints(true, null, null, step, units), label, help, showIf, enableIf);

    /// <summary>A selector for a per-player enum or <c>AcceptableValueList</c> setting; <paramref name="labels"/> are drawn for <paramref name="choices"/>.</summary>
    public SettingsPageBuilder Dropdown<T>(PlayerSetting<T> setting, IReadOnlyList<string>? choices = null, IReadOnlyList<string>? labels = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), SettingControl.Dropdown, ChoiceHints(true, choices, labels), label, help, showIf, enableIf);

    /// <summary>Every option visible at once, for a per-player enum or <c>AcceptableValueList</c> setting.</summary>
    public SettingsPageBuilder Radio<T>(PlayerSetting<T> setting, IReadOnlyList<string>? choices = null, IReadOnlyList<string>? labels = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), SettingControl.Radio, ChoiceHints(false, choices, labels), label, help, showIf, enableIf);

    /// <summary>A text field for a per-player <see cref="string"/> setting.</summary>
    public SettingsPageBuilder Text(PlayerSetting<string> setting, string? label = null, string? help = null,
        Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), SettingControl.Text, null, label, help, showIf, enableIf);

    /// <summary>A per-player setting with the control its type chooses.</summary>
    public SettingsPageBuilder Setting<T>(PlayerSetting<T> setting, string? label = null, string? help = null,
        Func<bool>? showIf = null, Func<bool>? enableIf = null) =>
        AddScoped(Id(setting), null, null, label, help, showIf, enableIf);

    /// <summary>A per-player item list: its size and <b>Edit</b>, which opens the list with the item picker.</summary>
    public SettingsPageBuilder ItemList(PlayerItemListSetting setting, string? label = null, string? help = null,
        Func<bool>? showIf = null, Func<bool>? enableIf = null)
    {
        if (setting == null) throw new ArgumentNullException(nameof(setting));
        return AddScoped(setting.Setting.RowId, null, null, label, help, showIf, enableIf);
    }

    /// <summary>A server-wide item list: admins edit it with the item picker; everyone else reads its size.</summary>
    public SettingsPageBuilder ItemList(ItemListSetting setting, string? label = null, string? help = null,
        Func<bool>? showIf = null, Func<bool>? enableIf = null)
    {
        if (setting == null) throw new ArgumentNullException(nameof(setting));
        var entry = setting.Entry;
        return AddScoped(_pluginId + "/" + entry.Definition.Section + "/" + entry.Definition.Key, null, null, label, help, showIf, enableIf);
    }

    private static string Id<T>(PlayerSetting<T> setting) =>
        (setting ?? throw new ArgumentNullException(nameof(setting))).RowId;
}
