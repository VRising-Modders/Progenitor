using System;
using System.Collections.Generic;
using Progenitor.Chat;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// Chat stage 3 (mod links): what <see cref="ModLinks"/> borrows from the workspace. A mod's Thunderstore
/// package comes from the install ledger first (<c>package:Namespace-Name@version</c>, what Progenitor wrote
/// when it installed the mod), then from the Install-tab listing that names the plugin by its manifest id or
/// inspected identity (<see cref="ResolveInstallLink"/>). A listing matched by name alone is never shared or
/// treated as installed. Opening a link only selects a page; nothing here downloads, installs or switches.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>The open and close reason of a mod link (the transition log names it).</summary>
    internal const string ReasonModLink = "mod-link";

    /// <summary>Readback key of the Share cell's package on a Mods list entry (<c>modsListEntry.share</c>).</summary>
    internal const string ShareReadbackKey = "share";

    /// <summary>Open the workspace for a link, the way the Open Mods key does (in a world through the game's menu).</summary>
    internal static void OpenForModLink()
    {
        if (IsOpen) return;
        if (ModsShortcutRoute.TryRoutePress(ReasonModLink)) return;
        Open(ReasonModLink);
    }

    /// <summary>The open reason of <see cref="PluginMenu.OpenPage"/> (the transition log names it).</summary>
    internal const string ReasonModPage = "mod-page";

    /// <summary>Whether <paramref name="pluginId"/> registered a custom page titled <paramref name="title"/>.</summary>
    internal static bool HasPage(string pluginId, string title) =>
        !string.IsNullOrWhiteSpace(pluginId) && !string.IsNullOrWhiteSpace(title)
        && Mods.TryGetValue(pluginId, out var mod) && !mod.IsDisposed && mod.Pages.ContainsKey(title);

    /// <summary>
    /// <see cref="PluginMenu.OpenPage"/>: open the Mods menu the way its key does (in a world through the game's menu,
    /// which finishes on a later frame), then show the mod's custom page on the Mods tab. Calling <see cref="OpenPage"/>
    /// straight away skips the game's menu, so in a world the player is left on its spellbook page (2026-10-09, the
    /// controls sample's Ctrl+F7). False, opening nothing, when there is no such page.
    /// </summary>
    internal static bool RequestModPage(string pluginId, string title)
    {
        if (!HasPage(pluginId, title)) return false;
        string id = pluginId.Trim();
        ModLinks.OpenWorkspaceThen(() => ShowModPage(id, title));
        return true;
    }

    /// <summary>With the workspace open, the Mods tab with the mod selected and its custom page shown.</summary>
    private static void ShowModPage(string pluginId, string title)
    {
        if (!IsOpen) return;
        // Unloaded while the menu was opening: the workspace stays where it opened.
        if (!HasPage(pluginId, title)) return;
        EnsureTabState();
        _tabState!.Mods.Selection = ModsWorkspaceIdentity.Installed(pluginId);
        if (_tabState.Selected != ModsTab.Mods) SelectWorkspaceTab(ModsTab.Mods);
        OpenPage(pluginId, title, ReasonModPage);
    }

    /// <summary>The installed packages and the catalog's state, read with the workspace open. Never throws.</summary>
    internal static ModLinkFacts ModLinkFacts()
    {
        var installed = new List<ModLinkInstalled>();
        try
        {
            foreach (var row in CurrentSidebarModel(applySearch: false).Rows)
            {
                // A server-only row is the joined server's mod, not one installed here; the loader is no mod.
                if (row.IsLoader || row.Provenance == ModProvenance.Server) continue;
                var (package, version) = SharePackageOf(row);
                if (package.Length > 0) installed.Add(new ModLinkInstalled(row.Id, package, version));
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod link: reading the installed mods threw: " + e.Message); }

        var catalog = _catalog;
        bool loaded = false, loading = false;
        if (catalog != null)
        {
            try { loaded = catalog.Search(null).Count > 0; }
            catch { loaded = false; }
            try { loading = catalog.Status.Kind is InstallStatusKind.LoadingIndex or InstallStatusKind.Fetching; }
            catch { loading = false; }
        }

        return new ModLinkFacts
        {
            Installed = installed,
            CatalogEnabled = ThunderstoreEnabled,
            CatalogLoaded = loaded,
            CatalogLoading = loading,
            Listing = package =>
            {
                if (catalog == null) return null;
                if (!catalog.TryGetEntry(package, out var entry) || entry == null || entry.Key.Length == 0) return null;
                return (entry.Key, entry.Manifest.Version ?? "");
            },
        };
    }

    /// <summary>A link to an installed mod: its page in the Mods tab, through the ordinary selection path.</summary>
    internal static void OpenModLinkInstalled(string pluginId)
    {
        if (!IsOpen || string.IsNullOrWhiteSpace(pluginId)) return;
        EnsureTabState();
        string id = pluginId.Trim();
        _tabState!.Mods.Selection = ModsWorkspaceIdentity.Installed(id);
        if (_tabState.Selected == ModsTab.Mods) OpenMod(id, ReasonModLink);
        else SelectWorkspaceTab(ModsTab.Mods);
    }

    /// <summary>
    /// A link to a mod that is not installed: its listing in Install Mods, the same jump as
    /// <b>Show in Install mods</b>. A search left on the tab is cleared so it cannot hide the listing.
    /// </summary>
    internal static void OpenModLinkCatalog(string catalogKey)
    {
        if (!IsOpen || string.IsNullOrWhiteSpace(catalogKey)) return;
        EnsureTabState();
        _tabState!.InstallMods.Search = "";
        OpenInstallFromInstalled("", catalogKey.Trim());
    }

    /// <summary>
    /// The Thunderstore package (<c>Namespace-Name</c>) and version an installed row can be shared as, or
    /// empty strings. The ledger's package source wins; else the listing that names the plugin's id or
    /// identity. Only a valid package id is returned (a Nexus or file listing key is not one).
    /// </summary>
    internal static (string Package, string Version) SharePackageOf(ModsSidebarRow row)
    {
        if (row == null || row.IsLoader) return ("", "");
        for (int i = 0; i < row.Ledger.Count; i++)
        {
            if (!InstalledState.TrySplitPackageSource(row.Ledger[i].Source, out string package, out string version)) continue;
            if (ModLinkToken.IsPackage(package)) return (package, ModLinkToken.IsVersion(version) ? version : "");
        }

        string key = "";
        try { key = ResolveInstallLink(row.Id)?.Key ?? ""; }
        catch { key = ""; }
        if (!ModLinkToken.IsPackage(key)) return ("", "");
        string installed = LedgerVersionOf(row);
        return (key, ModLinkToken.IsVersion(installed) ? installed : "");
    }

    /// <summary>The Share control of an installed mod (a Mods list row or its page).</summary>
    internal static string ShareInstalled(string pluginId)
    {
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        if (row == null) return ModLinks.ShareInvalid;
        var (package, version) = SharePackageOf(row);
        return package.Length == 0 ? ModLinks.ShareInvalid : ModLinks.Share(package, version.Length > 0 ? version : null);
    }

    /// <summary>
    /// The Share control of an Install Mods listing: the package without a version, so the link keeps
    /// pointing at the newest listing.
    /// </summary>
    internal static string ShareCatalog(string catalogKey) =>
        ModLinkToken.IsPackage(catalogKey) ? ModLinks.Share(catalogKey, null) : ModLinks.ShareInvalid;

    private static List<ChatModCandidate>? _suggestInstalled;
    private static double _suggestInstalledAt = double.MinValue;
    private static readonly System.Diagnostics.Stopwatch SuggestClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// CHAT11: mods the chat's <c>[mod:</c> suggestions can offer for <paramref name="partial"/>: the installed mods that
    /// have a Thunderstore package (read at most every 5 s), and the Thunderstore listings when the list is already loaded
    /// (the Mods menu loads it; chat never fetches). Never throws.
    /// </summary>
    internal static List<ChatModCandidate> ModLinkSuggestions(string partial)
    {
        var all = new List<ChatModCandidate>();
        double now = SuggestClock.Elapsed.TotalSeconds;
        if (_suggestInstalled == null || now - _suggestInstalledAt > 5.0)
        {
            var installed = new List<ChatModCandidate>();
            try
            {
                foreach (var row in CurrentSidebarModel(applySearch: false).Rows)
                {
                    if (row.IsLoader || row.Provenance == ModProvenance.Server) continue;
                    var (package, _) = SharePackageOf(row);
                    if (package.Length > 0) installed.Add(new ChatModCandidate(package, row.Name, installed: true));
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod suggestions: reading the installed mods threw: " + e.Message); }

            _suggestInstalled = installed;
            _suggestInstalledAt = now;
        }

        all.AddRange(_suggestInstalled);
        var catalog = _catalog;
        if (catalog != null)
        {
            try
            {
                int taken = 0;
                foreach (var entry in catalog.Search(partial.Length > 0 ? partial : null))
                {
                    if (!ModLinkToken.IsPackage(entry.Key)) continue;
                    all.Add(new ChatModCandidate(entry.Key, entry.SearchLabel, installed: false));
                    if (++taken >= 200) break;
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod suggestions: searching Thunderstore threw: " + e.Message); }
        }

        return all;
    }

    /// <summary>Whether the Thunderstore list is loaded for the chat's suggestions.</summary>
    internal static bool ModCatalogLoaded()
    {
        try { return _catalog != null && _catalog.Search(null).Count > 0; }
        catch { return false; }
    }

    /// <summary>The Install pane's share seam (<see cref="InstallModsPane.ShareInChat"/>).</summary>
    private static void OnInstallShare(string catalogKey) => ShareCatalog(catalogKey);
}
