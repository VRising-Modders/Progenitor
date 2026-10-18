using System;
using Progenitor.Networking;

namespace Progenitor.Ui;

public static partial class ModsMenu
{
    private static int _scopedSettingsRevision = -1;
    private static bool _scopedSettingsReady;
    private static bool _scopedSettingsAdmin;
    private static long _scopedSettingsRefreshAt;
    private static bool _scopedValuesDirty;
    private static void OnScopedSettingValue(string id, string value) => _scopedValuesDirty = true;

    private static void TickScopedSettings()
    {
        if (!IsOpen) return;
        long now = Environment.TickCount64;
        if (now < _scopedSettingsRefreshAt) return;
        _scopedSettingsRefreshAt = now + 250;
        bool changed = _scopedSettingsRevision != ScopedSettings.ClientRevision
            || _scopedSettingsReady != ScopedSettings.ClientReady
            || _scopedSettingsAdmin != ScopedSettings.ClientCanEditServerWide;
        _scopedSettingsRevision = ScopedSettings.ClientRevision;
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
