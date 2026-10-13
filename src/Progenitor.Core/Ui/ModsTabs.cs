using System;

namespace Progenitor.Ui;

/// <summary>
/// Owned tabs inside the Mods workspace. These are not native <c>OuterTabEntry</c>
/// objects and must not parent under <c>UICanvasBase</c>.
/// </summary>
internal enum ModsTab
{
    Mods = 0,
    InstallMods = 1,
    /// <summary>
    /// Three homes (2026-10-06): how the Mods window behaves on this PC (renamed from Configuration; the value is
    /// kept). After Keybinds.
    /// </summary>
    Preferences = 2,

    /// <summary>KEYS1: the game's keys (read-only) and every mod key, with conflicts and remapping.</summary>
    Keybinds = 3,

    /// <summary>Three homes (2026-10-06): how the joined server is set up (admin tools); the last tab.</summary>
    Server = 4,
}

/// <summary>
/// Which left list a tab may show. Installed, catalog and config-group identities are
/// three models; they must not share one sidebar instance.
/// </summary>
internal enum ModsSidebarKind
{
    /// <summary>Mods tab: discovered installed plugins.</summary>
    InstalledMods = 0,

    /// <summary>Install tab: catalog listings. Never mixed with installed plugin ids.</summary>
    CatalogListings = 1,

    /// <summary>
    /// Preferences and Server tabs: the tab's settings groups (three homes: a click scrolls to the group). Not a mod catalog.
    /// </summary>
    ConfigGroups = 2,

    None = 3,
}

/// <summary>Which kind of row identity a tab selection holds.</summary>
internal enum ModsIdentityKind
{
    InstalledPlugin = 0,
    CatalogListing = 1,
    ConfigGroup = 2,
}

/// <summary>Escape on a search field (clear, leave, then close); the close half is the existing workspace path.</summary>
internal enum ModsEscapeAction
{
    None = 0,
    ClearSearch = 1,
    CloseWorkspace = 2,

    /// <summary>SEARCHCLEAR1: the focused field is empty: it gives up focus; the workspace stays.</summary>
    LeaveSearch = 3,
}

/// <summary>
/// A selected row in one tab. Kind is part of equality: a catalog listing whose key
/// string matches a loaded plugin GUID is still not that plugin.
/// </summary>
internal readonly struct ModsWorkspaceIdentity : IEquatable<ModsWorkspaceIdentity>
{
    public ModsWorkspaceIdentity(ModsIdentityKind kind, string value)
    {
        Kind = kind;
        Value = value ?? "";
    }

    public ModsIdentityKind Kind { get; }

    public string Value { get; }

    public bool CanOpenAsLoadedPlugin =>
        Kind == ModsIdentityKind.InstalledPlugin && !string.IsNullOrWhiteSpace(Value);

    public static ModsWorkspaceIdentity Installed(string pluginId) =>
        new(ModsIdentityKind.InstalledPlugin, pluginId ?? "");

    public static ModsWorkspaceIdentity Catalog(string catalogKey) =>
        new(ModsIdentityKind.CatalogListing, catalogKey ?? "");

    public static ModsWorkspaceIdentity Config(string group) =>
        new(ModsIdentityKind.ConfigGroup, group ?? "");

    public bool Equals(ModsWorkspaceIdentity other) =>
        Kind == other.Kind
        && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is ModsWorkspaceIdentity other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? ""));

    public static bool operator ==(ModsWorkspaceIdentity a, ModsWorkspaceIdentity b) => a.Equals(b);

    public static bool operator !=(ModsWorkspaceIdentity a, ModsWorkspaceIdentity b) => !a.Equals(b);
}

/// <summary>Per-tab remembered search, selection and scroll. Independent across tabs.</summary>
internal sealed class ModsTabPageState
{
    public string Search { get; set; } = "";

    public ModsWorkspaceIdentity? Selection { get; set; }

    /// <summary>Sidebar list 0–1 position. 1 is the top, matching the installed-mod sidebar.</summary>
    public float Scroll { get; set; } = 1f;

    /// <summary>MODS1 (Mods tab only): is the <c>On this server</c> group expanded? Collapsed by default.</summary>
    public bool ServerGroupExpanded { get; set; }

    /// <summary>
    /// MODS2 (Mods tab only): is the last group, <c>Required (N)</c>, expanded? Collapsed by
    /// default — it holds the loader and Progenitor's own plugins, which a player does not manage.
    /// Replaces MODS1's <c>ShowDeveloper</c> toggle state.
    /// </summary>
    public bool RequiredGroupExpanded { get; set; }
}

/// <summary>Result of switching the owned workspace tab. Switching never requests Close.</summary>
internal readonly struct ModsTabSwitchResult
{
    public ModsTabSwitchResult(
        ModsTab previous,
        ModsTab current,
        bool workspaceStillOpen,
        bool inputCaptureHeld,
        bool requestedClose)
    {
        Previous = previous;
        Current = current;
        WorkspaceStillOpen = workspaceStillOpen;
        InputCaptureHeld = inputCaptureHeld;
        RequestedClose = requestedClose;
    }

    public ModsTab Previous { get; }
    public ModsTab Current { get; }
    public bool WorkspaceStillOpen { get; }
    public bool InputCaptureHeld { get; }

    /// <summary>Always false. Tab switches keep the workspace open.</summary>
    public bool RequestedClose { get; }
}

/// <summary>
/// Game-free tab model for the five-tab Mods workspace (KEYS1 added Keybinds, three homes the Server tab). No Unity types, no static
/// process state. The coordinator holds one instance while the workspace is open.
/// </summary>
internal sealed class ModsTabState
{
    public ModsTabState(bool workspaceOpen = true, bool inputCaptureHeld = true)
    {
        WorkspaceOpen = workspaceOpen;
        InputCaptureHeld = inputCaptureHeld;
        Mods = new ModsTabPageState();
        InstallMods = new ModsTabPageState();
        Configuration = new ModsTabPageState();
        Keybinds = new ModsTabPageState();
        Server = new ModsTabPageState();
        Selected = ModsTab.Mods;
    }

    public ModsTab Selected { get; private set; }

    public bool WorkspaceOpen { get; set; }

    /// <summary><c>WhileShown</c> capture stays registered across tab switches.</summary>
    public bool InputCaptureHeld { get; set; }

    public ModsTabPageState Mods { get; }

    public ModsTabPageState InstallMods { get; }

    public ModsTabPageState Configuration { get; }

    /// <summary>KEYS1: the Keybinds tab's remembered search and scroll.</summary>
    public ModsTabPageState Keybinds { get; }

    /// <summary>Three homes: the Server tab's remembered group.</summary>
    public ModsTabPageState Server { get; }

    public ModsTabPageState Page(ModsTab tab) => tab switch
    {
        ModsTab.InstallMods => InstallMods,
        ModsTab.Preferences => Configuration,
        ModsTab.Keybinds => Keybinds,
        ModsTab.Server => Server,
        _ => Mods,
    };

    public ModsTabPageState CurrentPage => Page(Selected);

    public static ModsSidebarKind SidebarFor(ModsTab tab) => tab switch
    {
        ModsTab.Mods => ModsSidebarKind.InstalledMods,
        ModsTab.InstallMods => ModsSidebarKind.CatalogListings,
        ModsTab.Preferences => ModsSidebarKind.ConfigGroups,
        ModsTab.Server => ModsSidebarKind.ConfigGroups,
        _ => ModsSidebarKind.None,
    };

    public ModsSidebarKind CurrentSidebar => SidebarFor(Selected);

    /// <summary>True only for the installed-mod and catalog sidebars, never for config groups.</summary>
    public static bool IsModCatalogueSidebar(ModsSidebarKind kind) =>
        kind is ModsSidebarKind.InstalledMods or ModsSidebarKind.CatalogListings;

    public static string Caption(ModsTab tab) => tab switch
    {
        ModsTab.InstallMods => ModsTabStrip.InstallModsCaption,
        ModsTab.Preferences => ModsTabStrip.PreferencesCaption,
        ModsTab.Keybinds => ModsTabStrip.KeybindsCaption,
        ModsTab.Server => ModsTabStrip.ServerCaption,
        _ => ModsTabStrip.ModsCaption,
    };

    public static string StatusName(ModsTab tab) => tab switch
    {
        ModsTab.InstallMods => "install",
        ModsTab.Preferences => "preferences",
        ModsTab.Keybinds => "keybinds",
        ModsTab.Server => "server",
        _ => "mods",
    };

    public static bool TryParseStatusName(string? name, out ModsTab tab)
    {
        tab = ModsTab.Mods;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim().ToLowerInvariant();
        if (n is "mods" or "installed") { tab = ModsTab.Mods; return true; }
        if (n is "install" or "install-mods" or "installmods") { tab = ModsTab.InstallMods; return true; }
        // Three homes: "configuration" stays an alias of the renamed tab.
        if (n is "preferences" or "prefs" or "configuration" or "config") { tab = ModsTab.Preferences; return true; }
        if (n is "server" or "server-settings") { tab = ModsTab.Server; return true; }
        if (n is "keybinds" or "keys" or "keybindings") { tab = ModsTab.Keybinds; return true; }
        return false;
    }

    /// <summary>
    /// Switch tabs. Keeps the workspace open, keeps input capture, and never requests
    /// <c>ModsMenu.Close()</c>. Per-tab search/selection/scroll are left as they were.
    /// </summary>
    public ModsTabSwitchResult SelectTab(ModsTab tab)
    {
        ModsTab previous = Selected;
        Selected = tab;
        return new ModsTabSwitchResult(
            previous,
            Selected,
            workspaceStillOpen: WorkspaceOpen,
            inputCaptureHeld: InputCaptureHeld,
            requestedClose: false);
    }

    /// <summary>
    /// Escape on a search field: a focused box with text clears, a focused empty box is left
    /// (SEARCHCLEAR1), anything else closes. The close half is the existing workspace hide/close
    /// path; this model does not call it.
    /// </summary>
    public ModsEscapeAction OnEscape(bool searchFocused, string? searchText)
    {
        switch (UiSearchEscape.Decide(searchFocused, searchText))
        {
            case UiSearchEscape.Decision.Clear:
                CurrentPage.Search = "";
                return ModsEscapeAction.ClearSearch;
            case UiSearchEscape.Decision.Leave:
                return ModsEscapeAction.LeaveSearch;
            default:
                return ModsEscapeAction.CloseWorkspace;
        }
    }

    /// <summary>
    /// A catalog listing can never open as a loaded plugin, even when its key string
    /// matches a plugin GUID.
    /// </summary>
    public static bool CatalogRowOpensAsLoadedPlugin(ModsWorkspaceIdentity identity) =>
        identity.Kind == ModsIdentityKind.CatalogListing && identity.CanOpenAsLoadedPlugin;
}
