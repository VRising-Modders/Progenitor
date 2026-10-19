using System;
using System.Text;
using Progenitor.Networking;

namespace Progenitor.Ui;

public static partial class ModsMenu
{
    private static int _scopedSettingsRevision = -1;
    private static bool _scopedSettingsReady;
    private static bool _scopedSettingsAdmin;
    private static string _scopedSettingsShape = "";
    private static long _scopedSettingsRefreshAt;
    private static bool _scopedValuesDirty;
    private static void OnScopedSettingValue(string id, string value) => _scopedValuesDirty = true;

    /// <summary>The server's mod list arrived or changed (Core's feature-table hook): rules that ask <c>ServerHasMod</c> are evaluated again.</summary>
    internal static void MarkScopedValuesDirty() => _scopedValuesDirty = true;

    /// <summary>
    /// What a page draws from the received catalog, values left out: a re-sent catalog with the same rows only refreshes
    /// values in place, so an open item-list editor or picker stays open.
    /// </summary>
    internal static string ScopedShape(System.Collections.Generic.IReadOnlyList<ScopedSettingRecord> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(r.Id).Append('\u0001').Append(r.Type).Append('\u0001').Append(r.Personal).Append(r.Editable)
                .Append('\u0001').Append(r.ModName).Append('\u0001').Append(r.Version).Append('\u0001').Append(r.Help)
                .Append('\u0001').Append(r.Default).Append('\u0001').Append(r.Min).Append('\u0001').Append(r.Max)
                .Append('\u0001').Append(r.MaxLength).Append('\u0001').Append(string.Join("\u0002", r.Choices));
            if (r.Presentation is { } p)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    using (var w = new System.IO.BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) p.Write(w, r.Choices.Length);
                    sb.Append('\u0001').Append(Convert.ToBase64String(ms.ToArray()));
                }
                catch { sb.Append("\u0001?"); }
            }
            sb.Append('\u0003');
        }
        return sb.ToString();
    }

    private static void TickScopedSettings()
    {
        if (!IsOpen) return;
        long now = Environment.TickCount64;
        if (now < _scopedSettingsRefreshAt) return;
        _scopedSettingsRefreshAt = now + 250;
        bool revised = _scopedSettingsRevision != ScopedSettings.ClientRevision;
        string shape = revised ? ScopedShape(ScopedSettingsRows.Records) : _scopedSettingsShape;
        bool changed = (revised && shape != _scopedSettingsShape)
            || _scopedSettingsReady != ScopedSettings.ClientReady
            || _scopedSettingsAdmin != ScopedSettings.ClientCanEditServerWide;
        if (revised && !changed) _scopedValuesDirty = true; // the same rows again: values only
        _scopedSettingsRevision = ScopedSettings.ClientRevision;
        _scopedSettingsShape = shape;
        _scopedSettingsReady = ScopedSettings.ClientReady;
        _scopedSettingsAdmin = ScopedSettings.ClientCanEditServerWide;
        if (changed)
        {
            _scopedSnapshot = null;
            if (_tabState?.Selected == ModsTab.Mods)
            {
                if (_showingSearch)
                {
                    // The server's catalog can add or remove mods from the list: rebuild it too (next
                    // frame, keeping the search field focused), not only the results.
                    UpdateSearchResults();
                    QueueSearchRebuild();
                    _scopedValuesDirty = false;
                    return;
                }
                var id = _selected;
                if (!string.IsNullOrEmpty(id)) RebuildModKeepingScroll(id!, "settings-manifest");
                RebuildSidebar("settings-manifest");
            }
        }
        else if (_scopedValuesDirty && _tabState?.Selected == ModsTab.Mods)
        {
            _modPage?.SettingsView?.Reevaluate(notify: false);
            foreach (var view in SearchViews) view.Reevaluate(notify: false);
        }
        _scopedValuesDirty = false;
    }
}
