using System;
using System.Globalization;
using System.Text.Json;
using Progenitor.Drops;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Game-free argument parsing for <c>bot_event</c> kind <c>dropall</c>, so the test project compiles
/// it. Optional <c>setting</c>: a <see cref="DropAllSetting"/> name (case-insensitive, e.g.
/// <c>"ExceptAnyEquipped"</c>) or the game's integer value; omitted means <c>All</c>. The result is
/// the raw <c>DropEntireInventorySetting</c> value to write, taken from the layout constants.
/// </summary>
internal static class BotDropAllArgs
{
    /// <summary>The setting names, in the game's order, for error messages.</summary>
    public const string Names =
        "All, ExceptAnyEquipped, ExceptEquippedInActionBar, ExceptAnyWeaponInActionBar, "
        + "ExceptAnyWeaponInActionBarOrAnyEquipped, ExceptAnythingInActionBar, ExceptAnythingInActionBarOrEquipped";

    public static DropAllSetting Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return DropAllSetting.All;
        if (args.TryGetProperty("slot", out _))
            throw new ArgumentException("dropall drops the whole bag; omit slot (use kind 'drop' for one stack)");
        if (!args.TryGetProperty("setting", out var el) || el.ValueKind == JsonValueKind.Null)
            return DropAllSetting.All;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (!el.TryGetInt32(out int n)) throw new ArgumentException($"args.setting must be 0..6 or a setting name ({Names})");
            return FromRaw(n);
        }
        if (el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"args.setting must be 0..6 or a setting name ({Names})");
        string text = (el.GetString() ?? "").Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return FromRaw(parsed);
        foreach (DropAllSetting s in Enum.GetValues(typeof(DropAllSetting)))
            if (s != DropAllSetting.Unknown && string.Equals(s.ToString(), text, StringComparison.OrdinalIgnoreCase))
                return s;
        throw new ArgumentException($"unknown setting '{text}' ({Names})");
    }

    /// <summary>The raw <c>DropEntireInventorySetting</c> value the event carries (layout constants).</summary>
    public static int Raw(DropAllSetting setting) => setting switch
    {
        DropAllSetting.All => Layout.DropEntireInventorySettingAll,
        DropAllSetting.ExceptAnyEquipped => Layout.DropEntireInventorySettingExceptAnyEquipped,
        DropAllSetting.ExceptEquippedInActionBar => Layout.DropEntireInventorySettingExceptEquippedInActionBar,
        DropAllSetting.ExceptAnyWeaponInActionBar => Layout.DropEntireInventorySettingExceptAnyWeaponInActionBar,
        DropAllSetting.ExceptAnyWeaponInActionBarOrAnyEquipped => Layout.DropEntireInventorySettingExceptAnyWeaponInActionBarOrAnyEquipped,
        DropAllSetting.ExceptAnythingInActionBar => Layout.DropEntireInventorySettingExceptAnythingInActionBar,
        DropAllSetting.ExceptAnythingInActionBarOrEquipped => Layout.DropEntireInventorySettingExceptAnythingInActionBarOrEquipped,
        _ => throw new ArgumentException($"setting {setting} has no game value"),
    };

    private static DropAllSetting FromRaw(int raw)
    {
        if (raw == Layout.DropEntireInventorySettingAll) return DropAllSetting.All;
        if (raw == Layout.DropEntireInventorySettingExceptAnyEquipped) return DropAllSetting.ExceptAnyEquipped;
        if (raw == Layout.DropEntireInventorySettingExceptEquippedInActionBar) return DropAllSetting.ExceptEquippedInActionBar;
        if (raw == Layout.DropEntireInventorySettingExceptAnyWeaponInActionBar) return DropAllSetting.ExceptAnyWeaponInActionBar;
        if (raw == Layout.DropEntireInventorySettingExceptAnyWeaponInActionBarOrAnyEquipped) return DropAllSetting.ExceptAnyWeaponInActionBarOrAnyEquipped;
        if (raw == Layout.DropEntireInventorySettingExceptAnythingInActionBar) return DropAllSetting.ExceptAnythingInActionBar;
        if (raw == Layout.DropEntireInventorySettingExceptAnythingInActionBarOrEquipped) return DropAllSetting.ExceptAnythingInActionBarOrEquipped;
        throw new ArgumentException($"args.setting {raw} is outside the game's values 0..6 ({Names})");
    }
}
