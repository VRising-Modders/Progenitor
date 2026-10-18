using System;
using System.Collections.Generic;
using System.Threading;
using Progenitor.Networking;

namespace Progenitor.Ui;

public static partial class ModsMenu
{
    private static readonly List<ModSettingsView> SearchViews = new();
    private static string? _searchReturnPlugin;
    private static float _searchReturnScroll = 1f, _searchReturnOffset;
    private static bool _showingSearch;
    private static string _searchReturnKind = "", _searchReturnPage = "";

    internal static List<ModSettingsSection> SearchSections(IReadOnlyList<ModSettingsSection> sections, string query)
    {
        var found = new List<ModSettingsSection>();
        foreach (var section in sections)
        {
            var items = new List<ModSettingsItem>();
            foreach (var item in section.Items)
            {
                if (item.Kind == ModSettingsItemKind.Divider) continue;
                if (item.Options.ShowIf != null)
                {
                    try { if (!item.Options.ShowIf()) continue; } catch { continue; }
                }
                if (MatchesSearch(section.Title, section.SourceTitle, query) || ModSettingsLayout.Matches(item, query)) items.Add(item);
            }
            if (items.Count > 0) found.Add(new ModSettingsSection(section.Title, section.Kind, items)
                { Scope = section.Scope, Owner = section.Owner, SourceTitle = section.SourceTitle });
        }
        return found;
    }

    private static bool MatchesModOrSettings(string id, string name) =>
        MatchesSearch(id, name, _search) || SettingsMatch(id);

    // Search matching reads each mod's sections once per settings revision instead of once per row
    // per keystroke. Only matching reads them (labels, sections, help, predicates); the results pane
    // builds fresh sections for the mods it shows, because a released view releases its items.
    private readonly record struct SettingsStamp(int List, int Pages, int Keys, int Sources, int Scoped);
    private static readonly Dictionary<string, (ModMenuRegistration? Mod, List<ModSettingsSection> Sections)> SearchSectionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static SettingsStamp _searchCacheStamp;
    private static int _settingsSectionsRevision;
    private static bool _searchResultsQueued;
    private static long _searchResultsDue;
    internal const int SearchResultsDelayMs = 200;

    /// <summary>How many times search matching built a mod's sections (tests).</summary>
    internal static int SearchSectionBuilds { get; private set; }

    private static SettingsStamp CurrentSettingsStamp() => new(
        Volatile.Read(ref _listGeneration), _settingsSectionsRevision, SettingsCatalog.KeyVersion,
        SettingsCatalog.SourceCount, ScopedSettings.ClientRevision);

    private static List<ModSettingsSection> CachedSettingsSections(string id)
    {
        var stamp = CurrentSettingsStamp();
        if (stamp != _searchCacheStamp) { SearchSectionCache.Clear(); _searchCacheStamp = stamp; }
        Mods.TryGetValue(id, out var mod);
        if (SearchSectionCache.TryGetValue(id, out var hit) && ReferenceEquals(hit.Mod, mod)) return hit.Sections;
        var sections = BuildSettingsSections(id, mod);
        SearchSectionBuilds++;
        SearchSectionCache[id] = (mod, sections);
        return sections;
    }

    private static bool SettingsMatch(string id) => SearchSections(CachedSettingsSections(id), _search).Count > 0;

    private static void QueueSearchResults()
    {
        _searchResultsDue = Environment.TickCount64 + SearchResultsDelayMs;
        if (_searchResultsQueued) return;
        _searchResultsQueued = true;
        Plugin.Frame += FlushSearchResults;
    }

    private static void FlushSearchResults()
    {
        if (Environment.TickCount64 < _searchResultsDue) return;
        CancelSearchResults();
        if (IsOpen) UpdateSearchResults();
    }

    private static void CancelSearchResults()
    {
        if (!_searchResultsQueued) return;
        _searchResultsQueued = false;
        try { Plugin.Frame -= FlushSearchResults; }
        catch { /* already unsubscribed */ }
    }

    private static void ReleaseSearchViews()
    {
        foreach (var view in SearchViews) view.Release();
        SearchViews.Clear();
    }

    private static void UpdateSearchResults()
    {
        if (_tabState != null && _tabState.Selected != ModsTab.Mods) return;
        if (_search.Length == 0)
        {
            if (!_showingSearch) return;
            _showingSearch = false;
            var id = _searchReturnPlugin;
            if (id == null) Open(ReasonUserButton); else if (_searchReturnKind == "custom") OpenPage(id, _searchReturnPage, ReasonUserButton); else OpenMod(id, ReasonUserButton);
            _contentScroll = _searchReturnScroll;
            _contentOffsetY = _searchReturnOffset;
            RestoreScroll();
            return;
        }
        if (!_showingSearch)
        {
            CaptureScroll();
            _searchReturnPlugin = _selected;
            _searchReturnKind = _pageKind;
            _searchReturnPage = _pageId;
            _searchReturnScroll = _contentScroll;
            _searchReturnOffset = _contentOffsetY;
        }
        _showingSearch = true;
        CloseContent();
        ReleaseModPage();
        var panel = Ui.Panel("Search results", Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(panel, sidebar: false);
        Owned.Adopt(panel);
        var matches = new List<(string Id, string Name, bool NameMatch, List<ModSettingsSection> Sections)>();
        int count = 0;
        foreach (var row in CurrentSidebarModel(applySearch: false).Rows)
        {
            bool nameMatch = MatchesSearch(row.Id, row.Name, _search);
            if (!nameMatch && !SettingsMatch(row.Id)) continue;
            Mods.TryGetValue(row.Id, out var mod);
            var sections = SearchSections(BuildSettingsSections(row.Id, mod), _search);
            foreach (var section in sections) count += section.Items.Count;
            matches.Add((row.Id, row.Name, nameMatch, sections));
        }
        matches.Sort((a,b) => a.NameMatch != b.NameMatch ? (a.NameMatch ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a.Name,b.Name));
        panel.Label($"{count} matching {(count == 1 ? "setting" : "settings")} in {matches.Count} {(matches.Count == 1 ? "mod" : "mods")}");
        if (matches.Count == 0) panel.Label("No matching mods or settings.");
        foreach (var match in matches)
        {
            string id = match.Id;
            panel.Button(match.Name + " — Open mod").OnClick(() => OpenSearchMod(id));
            var view = new ModSettingsView(id, match.Sections, "", () => ServerPartForSettings(id), () => SettingsScopeContextFor(id));
            view.SearchResult = true;
            view.ShowInMod = item => OpenSearchSetting(id, item);
            SearchViews.Add(view);
            view.Build(panel, (_, _, _) => { });
        }
        ShowWorkspacePanel(panel);
        FinishWorkspaceShow();
    }

    private static void OpenSearchSetting(string id, string item)
    {
        OpenSearchMod(id);
        var view = _modPage?.SettingsView;
        var panel = Owned.Held;
        if (view == null || panel == null) return;
        var handle = view.RevealSearchItem(item);
        if (handle == null) return;
        void Reveal()
        {
            if (!ReferenceEquals(panel, Owned.Held)) return;
            UiReveal.TryReveal(panel, handle, handle, toTop: true, out _, out _, out _);
        }
        Reveal();
        PostKeybindsStep(Reveal);
    }

    private static void OpenSearchMod(string id)
    {
        _showingSearch = false;
        TryChangeSearch("");
        OpenMod(id, ReasonUserButton);
    }
}
