using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>Adapts server-delivered schemas to the settings kit without loading a mod assembly.</summary>
internal static class ScopedSettingsRows
{
    internal static IReadOnlyList<ScopedSettingRecord> Records => ScopedSettings.ClientReady
        ? ScopedSettings.ClientRows : Array.Empty<ScopedSettingRecord>();

    internal static bool Has(string pluginId)
    {
        foreach (var row in Records)
            if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IntField(ScopedSettingRecord row) => row.Type is "System.Int32" or "System.Int16" or "System.Byte" or "System.SByte" or "System.UInt16";

    private static int Bound(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : fallback;

    internal static AdminSettingSpec Spec(ScopedSettingRecord row, ModSettingOptions? options = null) => new()
    {
        Manifest = true,
        Personal = row.Personal,
        PluginId = row.PluginId,
        SettingId = row.Id,
        Key = row.Id,
        Label = options?.Label ?? row.Key,
        Help = options?.Help ?? row.Help,
        DefaultValue = row.Default,
        Bounds = row.Min == null && row.Max == null ? "" : (row.Min ?? "unbounded") + " to " + (row.Max ?? "unbounded"),
        Min = Bound(row.Min, int.MinValue),
        Max = Bound(row.Max, int.MaxValue),
        RangeChecked = IntField(row),
        // Non-integer numbers use the text editor; the server parses and validates their exact type.
        Kind = row.Choices.Length > 0 ? AdminSettingKind.Choice
            : row.Type is "bool" or "Boolean" or "System.Boolean" ? AdminSettingKind.Toggle : IntField(row) ? AdminSettingKind.Number : AdminSettingKind.Text,
        Choices = row.Choices,
        ChoiceLabels = row.Choices,
        ChoiceRadio = options?.Control == SettingControl.Radio,
        Pending = () => options?.NeedsRestart == true && options.LoadValue != null && ScopedSettings.ReadClient(row.Id) != options.LoadValue,
        MaxLength = row.MaxLength > 0 ? row.MaxLength : 4096,
        Read = () => ScopedSettings.ReadClient(row.Id),
        ViewOnly = () => options?.Control == SettingControl.ReadOnly || !row.Editable || (!row.Personal && !ScopedSettings.ClientCanEditServerWide),
        ReadOnlyReason = () => !ScopedSettings.ClientReady ? "Not connected to a server."
            : !row.Editable || options?.Control == SettingControl.ReadOnly ? "This setting is read-only."
            : !row.Personal && !ScopedSettings.ClientCanEditServerWide ? SettingAvailability.AdminOnlyReason : null,
        SendExpected = (value, expected, done) => ScopedSettings.Request(row.Id, value, expected,
            result => done(result.Success, result.Success ? "Saved by the server." : result.Error)),
    };

    internal static List<ModSettingsSection> Merge(string pluginId, IReadOnlyList<ModSettingsSection> local)
    {
        var remote = new Dictionary<string, ScopedSettingRecord>(StringComparer.Ordinal);
        foreach (var row in Records)
            if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) remote[row.Id] = row;
        var result = new List<ModSettingsSection>();
        foreach (var section in local)
        {
            var items = new List<ModSettingsItem>();
            foreach (var item in section.Items)
            {
                if (remote.TryGetValue(item.Row?.RowId ?? item.Id, out var row))
                {
                    items.Add(MergeItem(item, row));
                    remote.Remove(row.Id);
                }
                else items.Add(item);
            }
            result.Add(new ModSettingsSection(section.Title, section.Kind, items));
        }
        var extra = new Dictionary<string, List<ModSettingsItem>>(StringComparer.Ordinal);
        foreach (var row in remote.Values)
        {
            if (!extra.TryGetValue(row.Section, out var items)) extra[row.Section] = items = new();
            items.Add(Item(row));
        }
        foreach (var group in extra) result.Add(new ModSettingsSection(group.Key, ModSettingsSectionKind.Group, group.Value));
        return result;
    }

    internal const string TypeMismatchReason = "The client and server setting types do not match. Update this mod to edit the setting.";

    internal static ModSettingsItem MergeItem(ModSettingsItem local, ScopedSettingRecord remote)
    {
        if (local.Row is { } row && !string.Equals(row.ValueType?.FullName, remote.Type, StringComparison.Ordinal))
        {
            var options = local.Options.Copy();
            options.CompatibilityReason = TypeMismatchReason;
            return ModSettingsItem.ForRow(row, options);
        }
        // Keep the authored predicates, restart baseline, labels, help and control hints intact.
        return Item(remote, local.Options);
    }

    internal static ModSettingsItem Item(ScopedSettingRecord row, ModSettingOptions? authored = null)
    {
        var options = authored?.Copy() ?? new ModSettingOptions
        {
            Scope = row.Personal ? SettingScope.Personal : SettingScope.ServerWide,
            Help = row.Help + " The default is " + (row.Default.Length == 0 ? "empty" : row.Default) + "."
                + (row.Min == null && row.Max == null ? "" : " Range: " + (row.Min ?? "unbounded") + " to " + (row.Max ?? "unbounded") + "."),
        };
        options.Help ??= row.Help;
        return ModSettingsItem.ForAdmin(row.Id, Spec(row, options), options);
    }

    internal const string ServerModExplanation = "Managed by the connected server.";

    internal static IReadOnlyList<ModMenuInventoryRow> Inventory(IReadOnlyList<ModMenuInventoryRow> local)
    {
        var rows = new List<ModMenuInventoryRow>(local);
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++) indices[rows[i].Id] = i;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Records)
        {
            if (!seen.Add(row.PluginId)) continue;
            bool exists = indices.TryGetValue(row.PluginId, out int index);
            var old = exists ? rows[index] : null;
            if (old != null && ModMenuInventory.IsLocalLocation(old.Location)) continue;
            var updated = new ModMenuInventoryRow
            {
                Id = row.PluginId,
                DisplayName = string.IsNullOrWhiteSpace(row.ModName) ? old?.DisplayName ?? row.PluginId : row.ModName,
                Version = row.Version, Location = "Server list", Status = "On server", ServerAsked = true,
                Requirement = old?.Requirement ?? "",
                Explanation = ServerModExplanation,
            };
            if (exists) rows[index] = updated;
            else rows.Add(updated);
        }
        return rows;
    }
}
