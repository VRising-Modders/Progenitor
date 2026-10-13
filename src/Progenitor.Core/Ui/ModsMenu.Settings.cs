using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization (docs/settings-organization.md): where Progenitor's own settings are drawn, the readback of
/// every place (<c>mods_ui settings_places</c>), and the links between the Configuration tab and Progenitor's page.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>
    /// The joined server's admin-channel values (<c>progenitor/JoinAdmin/&lt;op&gt;</c>) as this client last
    /// received them: <c>(value, display)</c>, or null before the server sent its state. Set on a client by
    /// <c>JoinServerSettings</c>; null (tests, a server) reads every admin-channel value as unknown.
    /// </summary>
    internal static Func<string, (string Value, string Display)?>? JoinAdminValueReader;

    /// <summary>Stage 6: the joined server's Server rules as its last <c>'PrSv'</c> state said (the password as set / not set).</summary>
    internal static Func<string, (string Value, string Display)?>? ServerRulesValueReader;

    /// <summary>
    /// <c>mods_ui settings_places</c>: <c>{settingId: {home, places[], value, display, known, store, source}}</c> for
    /// every placed setting of Progenitor's own (<see cref="SettingPlaces"/>), each value read from its one store now:
    /// the bound <c>ConfigEntry</c> for a cfg row, the server's last answer for an admin-channel row.
    /// </summary>
    public static Dictionary<string, object?> SettingsPlacesReadback()
    {
        var rows = new Dictionary<string, SettingsRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in SettingsCatalog.Rows())
                if (!rows.ContainsKey(row.RowId)) rows[row.RowId] = row;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings places catalog read threw: " + e.Message); }

        return SettingPlaces.Readback(place => ReadPlace(place, rows));
    }

    private static (string Value, string Display)? ReadPlace(SettingPlace place, Dictionary<string, SettingsRow> rows)
    {
        if (string.Equals(place.Store, SettingPlaces.StoreServerRules, StringComparison.Ordinal))
            return ServerRulesValueReader?.Invoke(place.SettingId.Substring(place.SettingId.LastIndexOf('/') + 1));
        if (string.Equals(place.Store, SettingPlaces.StoreJoinAdmin, StringComparison.Ordinal))
        {
            string op = place.SettingId.Substring(place.SettingId.LastIndexOf('/') + 1);
            return JoinAdminValueReader?.Invoke(op);
        }

        if (rows.TryGetValue(place.SettingId, out var row))
        {
            string value = row.Read();
            return (value, DisplayOf(row, value));
        }

        // Not listed by the catalog (a shared-cfg key the sweep has not reached): read the bound entry itself.
        string[] parts = place.SettingId.Split('/');
        if (parts.Length != 3) return null;
        var entry = SettingsCatalog.FindEntry(parts[0], parts[1], parts[2]);
        if (entry == null) return null;
        string text;
        try { text = SettingsEditors.Format(entry.BoxedValue); }
        catch { return null; }
        return (text, SettingPlaces.Display(text));
    }

    // ---- Three homes: the links between Preferences, Mods and Server; the remembered Install filters -------------

    /// <summary>
    /// Three homes: this player is an admin of the joined Progenitor server (connected, the handshake answered, and the
    /// server-wide <c>canEdit</c> the Server tab's rows use). The Install tab's <b>Server settings »</b> link shows then.
    /// </summary>
    internal static bool ServerTabAdmin()
    {
        try
        {
            var context = SettingsScopeContextFor(SettingsPanel.PluginId);
            return context.Connection == SettingConnection.Progenitor && context.CanEditServerWide;
        }
        catch { return false; }
    }

    /// <summary>
    /// Three homes: a link to the Server tab (Progenitor's page, the Install tab, the Preferences purpose line), through the
    /// ordinary tab path, at its top. The one per-frame observer records it as one place.
    /// </summary>
    internal static void OpenServerTabFromLink()
    {
        if (!IsOpen) return;
        EnsureTabState();
        _tabState!.Server.Selection = null;
        if (_tabState.Selected != ModsTab.Server) SelectWorkspaceTab(ModsTab.Server);
        else BuildServerPanes();
    }

    /// <summary>
    /// AUTOUPDATE3: the admin update notice's <b>View</b>: open the Mods window on the Server tab at its Updates group (the tab's
    /// top when the group cannot be found).
    /// </summary>
    internal static void OpenServerUpdates() => ModLinks.OpenWorkspaceThen(() =>
    {
        if (!IsOpen) return;
        OpenServerTabFromLink();
        try { JumpToGroup(ModsTab.Server, ProgenitorPage.UpdatesTitle); }
        catch { /* the tab's top */ }
    });

    /// <summary>Three homes: the Preferences purpose line's <b>Mods »</b>: the Mods tab, as its strip button opens it.</summary>
    internal static void OpenModsFromLink()
    {
        if (!IsOpen) return;
        EnsureTabState();
        if (_tabState!.Selected != ModsTab.Mods) SelectWorkspaceTab(ModsTab.Mods);
    }

    /// <summary>
    /// CHAT6: the chat quick menu's <b>All chat settings</b>: open the Mods menu on Progenitor's page at its Chat group
    /// (the page's top when the group cannot be found).
    /// </summary>
    internal static void OpenChatSettings() => ModLinks.OpenWorkspaceThen(JumpToChatSettings);

    private static void JumpToChatSettings()
    {
        if (!IsOpen) return;
        if (!_requiredExpanded)
        {
            _requiredExpanded = true;
            EnsureTabState();
            _tabState!.Mods.RequiredGroupExpanded = true;
        }

        if (!JumpToProgenitorGroup(ProgenitorPage.ChatTitle)) JumpToProgenitorPage();
    }

    /// <summary>Select Progenitor's page at its top (the Required group opened, so its left-list groups show).</summary>
    internal static void JumpToProgenitorPage()
    {
        if (!IsOpen) return;
        EnsureTabState();
        string id = SettingsPanel.PluginId;
        _tabState!.Mods.Selection = ModsWorkspaceIdentity.Installed(id);
        // A search left on the page must not hide what the link names.
        _settingsQuery = "";
        _settingsQueryPlugin = id;
        if (_tabState.Selected == ModsTab.Mods) OpenMod(id, ReasonUserButton);
        else SelectWorkspaceTab(ModsTab.Mods);
    }

    /// <summary>
    /// SERVERLINK1: the Server tab's <b>Progenitor's own server settings »</b>: the Mods tab on Progenitor's page, scrolled to
    /// its On this server group (the left-list jump, so the place history records it and Back returns to the Server tab).
    /// <paramref name="atGroup"/> false (this player sees none of those settings), or a page without the group: the page's
    /// top. Returns the landing (<see cref="SettingsLinks.LandingOnThisServer"/> or <see cref="SettingsLinks.LandingTop"/>),
    /// or empty when the menu is closed.
    /// </summary>
    internal static string OpenProgenitorServerSettingsFromLink(bool atGroup)
    {
        if (!IsOpen) return "";
        if (atGroup && JumpToProgenitorGroup(ProgenitorPage.OnThisServerTitle)) return SettingsLinks.LandingOnThisServer;
        if (!_requiredExpanded)
        {
            _requiredExpanded = true;
            EnsureTabState();
            _tabState!.Mods.RequiredGroupExpanded = true;
        }

        JumpToProgenitorPage();
        return SettingsLinks.LandingTop;
    }

    /// <summary>A link to the Preferences tab, at its top, through the ordinary tab path.</summary>
    internal static void OpenPreferencesFromLink()
    {
        if (!IsOpen) return;
        EnsureTabState();
        _tabState!.Configuration.Selection = null;
        if (_tabState.Selected != ModsTab.Preferences) SelectWorkspaceTab(ModsTab.Preferences);
        else BuildConfigurationPanes();
    }

    /// <summary>
    /// <c>[Settings] EditOtherModsSettings</c> changed: an open mod page is drawn again, so its foreign rows become
    /// editable (or read-only) at once. The Keybinds tab follows the catalog's key version on its own.
    /// </summary>
    internal static void NoteForeignEditPolicyChanged()
    {
        if (!IsOpen || _tabState == null || _tabState.Selected != ModsTab.Mods) return;
        string? id = _selected;
        if (string.IsNullOrEmpty(id) || _modPage == null) return;
        OpenMod(id!, "edit-other-mods-settings");
    }

    /// <summary>
    /// Stage 3: whether Progenitor's page draws a catalog row: the rows the places table puts on the page and new rows of
    /// Progenitor's own files; never a registered action (the Configuration tab's folder buttons), a row homed on the
    /// Configuration tab, a cfg-only row, or another row of Bootstrap's shared file.
    /// </summary>
    internal static bool ProgenitorPageShows(SettingsRow row)
    {
        if (row == null) return false;
        if (row.Kind == SettingsEditorKind.Action) return false;
        if (row.Kind == SettingsEditorKind.Keybind) return true;
        bool shared = false;
        try
        {
            var file = row.EntryForReadback?.ConfigFile;
            shared = file != null && Progenitor.Config.ConfigHelper.IsSharedConfigFile(file);
        }
        catch { shared = false; }
        return SettingPlaces.DrawnOnProgenitorPage(row.RowId, shared);
    }

    /// <summary>
    /// The Remember Install filters switch: the Install tab reads the remembered text when a session's list state is
    /// first made, and the menu saves the filters on a tab switch and on close while the switch is on.
    /// </summary>
    private static void WireRememberedFilters()
    {
        var bindings = _bindings;
        InstallModsPane.RememberedFiltersLoad = () =>
        {
            var b = bindings;
            if (b?.RememberInstallFilters == null || !b.RememberInstallFilters.Value) return null;
            return b.InstallFiltersRemembered?.Value;
        };
        InstallModsPane.RememberedFiltersSave = text =>
        {
            var b = bindings;
            if (b?.RememberInstallFilters == null || !b.RememberInstallFilters.Value || b.InstallFiltersRemembered == null) return;
            if (string.Equals(b.InstallFiltersRemembered.Value, text, StringComparison.Ordinal)) return;
            b.InstallFiltersRemembered.Value = text;
        };
    }

    /// <summary>Save the Install tab's filters and sort (a no-op unless the Remember switch is on).</summary>
    internal static void RememberInstallFilters()
    {
        try { _installPane?.SaveFilterMemory(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: remembering the Install filters threw: " + e.Message); }
    }

#if PROGENITOR_TESTS
    internal static ConfigurationPane? ConfigurationPaneForTests => _configPane;

    internal static ModsConfigBindings? BindingsForTests => _bindings;

    internal static string? InstallViewForTests => _bindings?.InstallView?.Value;
#endif

    /// <summary>A stored value as the row shows it: a choice's label, <c>on</c> / <c>off</c> for a switch, else the text.</summary>
    private static string DisplayOf(SettingsRow row, string value)
    {
        try
        {
            foreach (var choice in ModSettingsLayout.ChoicesFor(row))
                if (string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)) return choice.Label;
        }
        catch { /* the plain text */ }

        return SettingPlaces.Display(value);
    }
}
