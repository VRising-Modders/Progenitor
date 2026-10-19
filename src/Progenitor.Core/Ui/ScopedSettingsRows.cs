using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// Adapts server-delivered schemas to the settings kit without loading a mod assembly. A row's control, label,
/// section, order, units, choice words and show/enable rules come from the version 2 presentation hints when the server
/// sent them (<see cref="ScopedSettingRecord.Presentation"/>); without them each type gets its default editor.
/// </summary>
internal static class ScopedSettingsRows
{
    internal static IReadOnlyList<ScopedSettingRecord> Records => ScopedSettings.ClientReady
        ? ScopedSettings.ClientRows : Array.Empty<ScopedSettingRecord>();

    /// <summary>A choice row with at most this many options is drawn with every option visible (radio) by default.</summary>
    internal const int RadioChoicesAtMost = 4;

    internal static bool Has(string pluginId)
    {
        foreach (var row in Records)
            if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static ScopedSettingRecord? Find(string id)
    {
        foreach (var row in Records) if (string.Equals(row.Id, id, StringComparison.Ordinal)) return row;
        return null;
    }

    private static bool IntField(ScopedSettingRecord row) => row.Type is "System.Int32" or "System.Int16" or "System.Byte" or "System.SByte" or "System.UInt16";

    private static bool RealField(ScopedSettingRecord row) => row.Type is "System.Single" or "System.Double";

    private static int Bound(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : fallback;

    private static bool Real(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// The control for <paramref name="row"/>: the author's local control, else the server's hint, else the type's
    /// default. An item list is always an item list (its storage is text).
    /// </summary>
    internal static ScopedWidget WidgetFor(ScopedSettingRecord row, ModSettingOptions? options, ScopedSettingPresentation? hints)
    {
        if (row.Presentation?.Widget == ScopedWidget.ItemList || hints?.Widget == ScopedWidget.ItemList) return ScopedWidget.ItemList;
        var authored = ScopedSettingPresentation.WidgetOf(options?.Control);
        return authored != ScopedWidget.Default ? authored : hints?.Widget ?? ScopedWidget.Default;
    }

    internal static AdminSettingSpec Spec(ScopedSettingRecord row, ModSettingOptions? options = null, ScopedSettingPresentation? hints = null)
    {
        hints ??= row.Presentation;
        var widget = WidgetFor(row, options, hints);
        bool hasMin = Real(row.Min, out double min), hasMax = Real(row.Max, out double max);
        bool slider = widget == ScopedWidget.Slider && hasMin && hasMax && max > min && (IntField(row) || RealField(row));
        string[] labels = hints != null && hints.ChoiceLabels.Length == row.Choices.Length && row.Choices.Length > 0
            ? hints.ChoiceLabels : row.Choices;
        double step = Real(hints?.Step, out double s) && s > 0 ? s : IntField(row) ? 1 : slider ? (max - min) / 100 : 1;
        var kind = widget == ScopedWidget.ItemList ? AdminSettingKind.ItemList
            : row.Choices.Length > 0 ? AdminSettingKind.Choice
            : row.Type is "bool" or "Boolean" or "System.Boolean" ? AdminSettingKind.Toggle
            : slider ? AdminSettingKind.Slider
            : IntField(row) ? AdminSettingKind.Number : AdminSettingKind.Text;
        return new AdminSettingSpec
        {
            Manifest = true,
            Personal = row.Personal,
            PluginId = row.PluginId,
            SettingId = row.Id,
            Key = row.Id,
            Label = options?.Label ?? (hints != null && hints.Label.Length > 0 ? hints.Label : row.Key),
            Help = options?.Help ?? row.Help,
            DefaultValue = row.Default,
            Bounds = row.Min == null && row.Max == null ? "" : (row.Min ?? "unbounded") + " to " + (row.Max ?? "unbounded"),
            Min = Bound(row.Min, int.MinValue),
            Max = Bound(row.Max, int.MaxValue),
            SliderMin = hasMin ? min : 0,
            SliderMax = hasMax ? max : 0,
            Step = IntField(row) ? Math.Max(1, Math.Round(step)) : step,
            Integer = IntField(row),
            Units = hints?.Units ?? "",
            RangeChecked = IntField(row),
            // Non-integer numbers use the text editor unless drawn as a slider; the server parses and validates their exact type.
            Kind = kind,
            Choices = row.Choices,
            ChoiceLabels = labels,
            // Two to four options are drawn all at once (a radio list or segmented picker) unless a dropdown was asked for.
            ChoiceRadio = widget == ScopedWidget.Radio
                || (widget == ScopedWidget.Default && row.Choices.Length > 0 && row.Choices.Length <= RadioChoicesAtMost),
            ListHints = kind == AdminSettingKind.ItemList ? row.Presentation ?? hints : null,
            Display = kind == AdminSettingKind.ItemList ? ModSettingsView.ItemListSummary : null,
            Note = kind == AdminSettingKind.ItemList ? () => ModSettingsView.ItemListSummary(ScopedSettings.ReadClient(row.Id)) + "." : null,
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
    }

    internal static List<ModSettingsSection> Merge(string pluginId, IReadOnlyList<ModSettingsSection> local)
    {
        var remote = new Dictionary<string, ScopedSettingRecord>(StringComparer.Ordinal);
        var order = new List<ScopedSettingRecord>();
        foreach (var row in Records)
            if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) && !remote.ContainsKey(row.Id))
            {
                remote[row.Id] = row;
                order.Add(row);
            }
        var result = new List<ModSettingsSection>();
        foreach (var section in local)
        {
            var items = new List<ModSettingsItem>();
            foreach (var item in section.Items)
            {
                // A published setting the page placed by id is already drawn from the server's definition.
                if (item.Admin is { Manifest: true })
                {
                    remote.Remove(item.Id);
                    items.Add(item);
                }
                else if (remote.TryGetValue(item.Row?.RowId ?? item.Id, out var row))
                {
                    items.Add(MergeItem(item, row));
                    remote.Remove(row.Id);
                }
                else items.Add(item);
            }
            result.Add(new ModSettingsSection(section.Title, section.Kind, items));
        }

        // The rest, grouped by the page section the server named (else the cfg section), in the page's order.
        var groups = new List<(string Title, int Order, int First, List<(int Order, int Index, ScopedSettingRecord Row)> Rows)>();
        for (int i = 0; i < order.Count; i++)
        {
            var row = order[i];
            if (!remote.ContainsKey(row.Id)) continue;
            var hints = row.Presentation;
            string title = hints != null && hints.SectionTitle.Length > 0 ? hints.SectionTitle : row.Section;
            int sectionOrder = hints != null && hints.SectionOrder >= 0 ? hints.SectionOrder : int.MaxValue;
            int at = groups.FindIndex(g => string.Equals(g.Title, title, StringComparison.Ordinal));
            if (at < 0)
            {
                groups.Add((title, sectionOrder, i, new()));
                at = groups.Count - 1;
            }
            else if (sectionOrder < groups[at].Order) groups[at] = (groups[at].Title, sectionOrder, groups[at].First, groups[at].Rows);
            groups[at].Rows.Add((hints != null && hints.RowOrder >= 0 ? hints.RowOrder : int.MaxValue, i, row));
        }
        groups.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.First.CompareTo(b.First));
        foreach (var group in groups)
        {
            group.Rows.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));
            var items = new List<ModSettingsItem>(group.Rows.Count);
            foreach (var r in group.Rows) items.Add(Item(r.Row));
            int same = result.FindIndex(s => s.Kind == ModSettingsSectionKind.Group && string.Equals(s.Title, group.Title, StringComparison.Ordinal));
            if (same >= 0)
            {
                var merged = new List<ModSettingsItem>(result[same].Items);
                merged.AddRange(items);
                result[same] = new ModSettingsSection(group.Title, ModSettingsSectionKind.Group, merged);
            }
            else result.Add(new ModSettingsSection(group.Title, ModSettingsSectionKind.Group, items));
        }
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
        // Keep the authored predicates, restart baseline, labels, help and control hints intact; the local page's
        // words (choice labels, units, step) win over the server's hints field by field.
        var localHints = ScopedSettingPresentation.Derive(remote, local.Options, local.Row?.Presentation, false, "", -1, -1, null);
        return Item(remote, local.Options, Overlay(remote.Presentation, localHints));
    }

    /// <summary>A published setting the page placed by id (<c>page.Toggle(playerSetting)</c>), or null while the server has not sent it.</summary>
    internal static ModSettingsItem? Placed(PageItemSpec item)
    {
        var id = item.Options.ScopedRowId;
        if (id == null || Find(id) is not { } row) return null;
        var localHints = ScopedSettingPresentation.Derive(row, item.Options, item.Presentation, false, "", -1, -1, null);
        return Item(row, item.Options, Overlay(row.Presentation, localHints));
    }

    /// <summary>The server's hints with the local author's set fields laid over them.</summary>
    internal static ScopedSettingPresentation? Overlay(ScopedSettingPresentation? remote, ScopedSettingPresentation local)
    {
        if (remote == null) return local.IsEmpty ? null : local;
        var p = remote.Copy();
        if (local.Widget != ScopedWidget.Default && p.Widget != ScopedWidget.ItemList) p.Widget = local.Widget;
        if (local.Label.Length > 0) p.Label = local.Label;
        if (local.Units.Length > 0) p.Units = local.Units;
        if (local.Step != null) p.Step = local.Step;
        if (local.ChoiceLabels.Length > 0) p.ChoiceLabels = local.ChoiceLabels;
        return p;
    }

    internal static ModSettingsItem Item(ScopedSettingRecord row, ModSettingOptions? authored = null, ScopedSettingPresentation? hints = null)
    {
        var options = authored?.Copy() ?? new ModSettingOptions
        {
            Scope = row.Personal ? SettingScope.Personal : SettingScope.ServerWide,
        };
        var spec = Spec(row, options, hints);
        if (authored == null)
        {
            // Help: the server's line, then the default and range as a person reads them.
            string shown = row.Default.Length == 0 ? "empty" : spec.DisplayOf(row.Default);
            options.Help = row.Help + " The default is " + shown + "."
                + (row.Min == null && row.Max == null ? "" : " Range: " + (row.Min ?? "unbounded") + " to " + (row.Max ?? "unbounded")
                    + (spec.Units.Length > 0 ? " " + spec.Units : "") + ".");
            // Rules the server sent are evaluated against the values it sent.
            var rules = hints ?? row.Presentation;
            if (rules?.ShowIf is { } show) options.ShowIf = Remote(row.PluginId, show);
            if (rules?.EnableIf is { } enable) options.EnableIf = Remote(row.PluginId, enable);
            spec = Spec(row, options, hints);
        }
        options.Help ??= row.Help;
        return ModSettingsItem.ForAdmin(row.Id, spec, options);
    }

    private static Func<bool> Remote(string pluginId, SettingCondition condition) =>
        () => condition.Evaluate(c => ScopedSettings.ReadClient(pluginId + "/" + c.Section + "/" + c.Key), _ => null);

    /// <summary>
    /// A local rule's value of <paramref name="c"/>: the entry it names, else <paramref name="pluginId"/>'s bound entry with
    /// that section and key, else the value the joined server sent for it (a <c>PerPlayer</c> setting).
    /// </summary>
    internal static string? LocalValue(string pluginId, SettingCondition c)
    {
        try
        {
            if (c.Entry is { } entry) return SettingsEditors.Format(entry.BoxedValue);
            if (SettingsCatalog.FindEntry(pluginId, c.Section, c.Key) is { } found) return SettingsEditors.Format(found.BoxedValue);
        }
        catch { /* the received value below */ }
        return ScopedSettings.ReadClient(pluginId + "/" + c.Section + "/" + c.Key);
    }

    /// <summary>The joined server's answer to <c>ServerHasMod(<paramref name="modId"/>)</c>, from any rule of the mod it sent; null when it sent none.</summary>
    internal static bool? ServerFact(string pluginId, string modId)
    {
        foreach (var row in Records)
        {
            if (!string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) || row.Presentation == null) continue;
            if (Answer(row.Presentation.ShowIf, modId) is bool a) return a;
            if (Answer(row.Presentation.EnableIf, modId) is bool b) return b;
        }
        return null;
    }

    private static bool? Answer(SettingCondition? c, string modId)
    {
        if (c == null) return null;
        if (c.Kind == SettingCondition.Op.ServerMod && string.Equals(c.Key, modId, StringComparison.OrdinalIgnoreCase) && c.Answer != null)
            return c.Answer;
        foreach (var p in c.Parts) if (Answer(p, modId) is bool found) return found;
        return null;
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
