using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

// PrSt version 2: the presentation hints of each published row, derived on the server when a snapshot is built.
internal static partial class ScopedSettings
{
    private static readonly HashSet<string> WatchedMods = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedConditions = new(StringComparer.Ordinal);
    private static HashSet<string>? _loadedMods;
    private static double _loadedModsAt = double.NegativeInfinity;
    private static int _presentationEpoch = int.MinValue;
    private static string _factsSignature = "";

    /// <summary>
    /// The record one peer is sent for <paramref name="entry"/>: its current value and editability, the page's or
    /// catalog's help line, and (for a version 2 peer, <paramref name="hints"/>) the presentation hints.
    /// </summary>
    private static ScopedSettingRecord ForPeer(Registration entry, ulong account, bool hints, double now)
    {
        var row = ScopedSettingsSnapshot.Clone(entry.Schema);
        row.Value = entry.Read(account);
        row.Editable = row.Personal || ServerSettings.IsEditable(row.Id);
        PageItemSpec? item = null;
        string title = "";
        int sectionOrder = -1, rowOrder = -1;
        try
        {
            if (!ModSettingsPages.TryPlacement(row.PluginId, row.Id, out item, out title, out sectionOrder, out rowOrder)) item = null;
        }
        catch { item = null; }
        SettingPresentation? catalog = entry.Catalog;
        if (catalog == null)
        {
            try { catalog = SettingsCatalog.PresentationFor(row.PluginId, row.Section, row.Key); }
            catch { catalog = null; }
        }
        row.Help = ScopedSettingPresentation.Clip(ScopedSettingPresentation.HelpFor(row.Help, item, catalog), ScopedSettingsWire.MaxFieldBytes);
        if (!hints) return row;
        var p = ScopedSettingPresentation.Derive(row, item, title, sectionOrder, rowOrder, catalog);
        p.ShowIf = Publishable(row, p.ShowIf, now);
        p.EnableIf = Publishable(row, p.EnableIf, now);
        if (ItemListSettings.TryGet(row.Id, out var rules)) DescribeList(p, rules);
        row.Presentation = p.IsEmpty ? null : p;
        return row;
    }

    /// <summary>
    /// A condition as sent: every setting it names must be a published setting of the same mod (else it is dropped,
    /// with one warning), and its server facts carry the server's answers.
    /// </summary>
    private static SettingCondition? Publishable(ScopedSettingRecord row, SettingCondition? condition, double now)
    {
        if (condition == null) return null;
        foreach (var (section, key) in condition.References())
        {
            string id;
            try { id = RowId(row.PluginId, section, key); }
            catch { id = ""; }
            if (id.Length > 0 && Entries.ContainsKey(id)) continue;
            if (WarnedConditions.Add(row.Id + "|" + section + "/" + key))
                Plugin.Logger?.LogWarning("Settings: the rule on " + row.Id + " names " + section + "/" + key
                    + ", which this mod does not publish; clients without the mod's DLL ignore the rule.");
            return null;
        }
        foreach (var mod in condition.ServerMods()) WatchedMods.Add(mod);
        return condition.Resolved(mod => ServerRuns(mod, now));
    }

    private static void DescribeList(ScopedSettingPresentation p, ItemListRules rules)
    {
        p.Widget = ScopedWidget.ItemList;
        p.AllowCategories = rules.AllowCategories;
        p.MaxEntries = rules.MaxEntries;
        p.MaxCount = rules.MaxCount;
        p.CountLabel = ScopedSettingPresentation.Clip(rules.CountLabel, ScopedSettingPresentation.MaxUnitsBytes);
        if (!rules.AllowCategories || rules.Categories == null) return;
        var ids = new List<string>();
        var labels = new List<string>();
        foreach (var pair in Categories(rules))
        {
            if (ids.Count >= ScopedSettingPresentation.MaxCategories) break;
            string id = (pair.Key ?? "").Trim();
            if (!ItemLimitList.IsCategoryId(id) || ids.Exists(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) continue;
            ids.Add(id);
            string label = ScopedSettingPresentation.Clip(pair.Value ?? "", ScopedSettingPresentation.MaxCategoryLabelBytes);
            labels.Add(label.Length > 0 ? label : id);
        }
        p.CategoryIds = ids.ToArray();
        p.CategoryLabels = labels.ToArray();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Categories(ItemListRules rules)
    {
        try { return rules.Categories?.Invoke() ?? Array.Empty<KeyValuePair<string, string>>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Settings: an item list's categories threw: " + e.Message);
            return Array.Empty<KeyValuePair<string, string>>();
        }
    }

    /// <summary>Whether this server runs the plugin <paramref name="guid"/> (the loaded mod list, read at most once a second).</summary>
    private static bool ServerRuns(string guid, double now)
    {
        if (_loadedMods == null || now - _loadedModsAt >= 1 || now < _loadedModsAt)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var mod in ModManifest.EnumerateLoaded()) if (!string.IsNullOrEmpty(mod.Id)) set.Add(mod.Id); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Settings: the loaded mod list could not be read: " + e.Message); }
            _loadedMods = set;
            _loadedModsAt = now;
        }
        return _loadedMods.Contains(guid);
    }

    /// <summary>
    /// Once a second while peers are connected: a page or catalog presentation that changed, a server fact a rule asks
    /// about, or an item list's categories changing re-sends the catalog.
    /// </summary>
    private static void CheckPresentation(double now)
    {
        int epoch = unchecked(ModSettingsPages.Epoch * 397 + SettingsCatalog.BindingGeneration);
        if (epoch != _presentationEpoch) { _presentationEpoch = epoch; _schemaRevision++; }
        var sb = new StringBuilder();
        foreach (var mod in WatchedMods) sb.Append(mod).Append(ServerRuns(mod, now) ? '1' : '0').Append(';');
        foreach (var entry in Entries.Values)
        {
            if (!ItemListSettings.TryGet(entry.Schema.Id, out var rules) || !rules.AllowCategories || rules.Categories == null) continue;
            sb.Append(entry.Schema.Id).Append(':');
            foreach (var pair in Categories(rules)) sb.Append(pair.Key).Append('=').Append(pair.Value).Append(',');
            sb.Append(';');
        }
        string signature = sb.ToString();
        if (signature != _factsSignature) { _factsSignature = signature; _schemaRevision++; }
    }

    private static void ResetPresentation()
    {
        WatchedMods.Clear(); WarnedConditions.Clear(); _loadedMods = null; _loadedModsAt = double.NegativeInfinity;
        _presentationEpoch = int.MinValue; _factsSignature = "";
    }

    /// <summary>A row packet for a peer: version 2 with hints, falling back to the row without them when they do not fit.</summary>
    private static byte[] RowPacket(Guid generation, ScopedSettingRecord row, bool hints)
    {
        byte version = hints ? ScopedSettingsWire.PresentationVersion : ScopedSettingsWire.Version;
        try { return ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = generation, Row = row }, version); }
        catch (ArgumentException e) when (row.Presentation != null)
        {
            Plugin.Logger?.LogWarning("Settings: presentation of " + row.Id + " omitted: " + e.Message);
            row.Presentation = null;
            return ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = generation, Row = row }, version);
        }
    }
}
