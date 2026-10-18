using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>INSTALLSIMPLE1: player-facing words of the Simple view and the view switch.</summary>
internal static class InstallSimpleCopy
{
    public const string ViewSimple = "Simple";
    public const string ViewAdvanced = "Advanced";
    public const string Explanation = "Simple chooses the client, server, or both from the mod's requirements and what's already installed. Confirmations show where changes apply. Use Advanced to manage each side separately.";

    public const string ShowUpdates = "Updates";
    public const string HeaderVersion = "Version";
    public const string HeaderStatus = "Status";

    /// <summary>
    /// Settings organization stage 8: the Simple toolbar's link to Progenitor's Server settings. The spec's <c>›</c> is drawn
    /// as <c>»</c>, the Latin-1 glyph the store link already uses: the game font is not known to have U+203A.
    /// </summary>
    public const string ServerSettingsLink = "Server settings \u00BB";

    /// <summary>SIMPLESCOPE1: the Simple drawer's muted note on a both-sides mod this player acts on only for this PC.</summary>
    public static string ServerHalfManaged(string server) =>
        "The server half runs on " + server + "; its admins manage it. Simple installs and removes only this PC's copy.";

    /// <summary>SIMPLESCOPE1: the <c>Not changed</c> fact of a confirmation that leaves a side alone.</summary>
    public const string FactNotChanged = "Not changed";

    public const string ClientKeepsCopy = "This client: its copy stays as it is";
    public const string ClientHasNone = "This client: nothing is installed here";

    public static string ServerKeepsCopy(string server) => server + ": its copy stays as it is";

    public static string ServerAdminsManage(string server) => server + ": its admins manage the server half";

    /// <summary>F14: an admin's this-PC-only action on a both-sides mod the server has no copy of.</summary>
    public static string ServerHasNone(string server) => server + " (it doesn't have this mod)";

    /// <summary>F14: the same while nothing has said whether the server has a copy (or its copy is being removed).</summary>
    public static string ServerUnchanged(string server) => server + ": nothing changes there";
}

/// <summary>
/// INSTALLSIMPLE1 (decided 2026-10-04): the Simple view of the Install mods tab — one row per
/// mod (icon, Mod, Version, Status, one button), the Show chips All / Installed / Updates, the search
/// and the drawer. Advanced is the per-side screen unchanged; a view switch on the search row picks
/// between them and the choice is remembered in <c>[Mods] InstallView</c>. The Simple row is combined
/// from the same per-side decision the Advanced row draws (<see cref="InstallSimpleRules"/>), and its
/// button runs the same flows and confirmations the side buttons do.
/// </summary>
internal sealed partial class InstallModsPane
{
    /// <summary>The toolbar's view switch: two chips at the left of the search row.</summary>
    public const string CellViewSimple = "view-simple";

    /// <inheritdoc cref="CellViewSimple"/>
    public const string CellViewAdvanced = "view-advanced";

    /// <summary>
    /// Settings organization stage 8 (decision 7): the toolbar's <b>Server settings »</b> link, in Simple's server context
    /// row while connected (see <see cref="ServerSettingsLinkShown"/>). Clicked with <c>widget_click label=simple-server/server-settings</c>.
    /// </summary>
    public const string CellServerSettings = "server-settings";

    /// <summary>Whether this player is an admin of the joined Progenitor server (live: the Server settings rows' fact). Not a gate for the Server settings link.</summary>
    internal Func<bool>? ServerSettingsAdminProbe { get; set; }

    /// <summary>Three homes: opens the Server tab (live: <c>ModsMenu.OpenServerTabFromLink</c>). Null: no link.</summary>
    internal Action? OpenServerSettings { get; set; }

    /// <summary>
    /// The link is drawn: the Simple view while connected, for every player there. The Server tab shows read-only access
    /// to a player who is not an admin and its own empty state on a server without Progenitor, so the link is not gated
    /// on admin (<see cref="ServerSettingsAdminProbe"/> does not decide it).
    /// </summary>
    internal bool ServerSettingsLinkShown => IsSimple && OpenServerSettings != null && PickerInputs?.Connected == true;

    internal IUiCellRow? SimpleServerRow { get; private set; }

    /// <summary>
    /// The server context row's second line. <c>Remote management</c> only on the remote route; a connected server with a
    /// blocked route (no Progenitor, remote installs off, status pending, not an admin) names the route's reason instead.
    /// </summary>
    internal static string SimpleServerState(ServerRoute route, bool connected) =>
        route.Block == ServerRouteBlock.Disconnected ? "Disconnected · Connect to manage"
            : route.Kind == ServerRouteKind.Local ? ServerRouteCopy.LocalManagement
            : connected ? "Connected" + (route.ManagementLabel is { Length: > 0 } management ? " · " + management : "")
            : "No server selected";

    private void BuildSimpleServerContext(IUiPanel panel)
    {
        var route = CurrentRoute;
        bool connected = PickerInputs?.Connected == true;
        string state = SimpleServerState(route, connected);
        string name = PickedServer?.ShortName ?? ServerIdentityFor(route.Kind == ServerRouteKind.Remote).ShortName;
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "server-context").With(
                text: (route.Kind == ServerRouteKind.None && !connected && !route.ForcedRemote ? "Server" : "Server: " + name) + "\n" + state,
                width: CellWidth.Fill(200f), wrap: CellWrap.Wrap),
        };
        if (ServerSettingsLinkShown) cells.Add(ServerSettingsLinkCell());
        var row = panel.CellRow(new CellRowSpec("simple-server", cells, height: 60f, plain: true));
        SimpleServerRow = row;
        row.OnClick(CellServerSettings, () => PressServerSettings());
        panel.CellRow(new CellRowSpec("simple-help", new[]
        {
            new CellSpec(CellKind.Muted, "explanation").With(text: InstallSimpleCopy.Explanation,
                width: CellWidth.Fill(200f), wrap: CellWrap.Wrap),
        }, height: 52f, plain: true));
    }

    /// <summary>The link's cell: a key cap at the right of the search row.</summary>
    internal static CellSpec ServerSettingsLinkCell()
    {
        float width = ChipNaturalWidth(InstallSimpleCopy.ServerSettingsLink);
        return new CellSpec(CellKind.Button, CellServerSettings).With(text: InstallSimpleCopy.ServerSettingsLink,
            width: CellWidth.Px(width), minWidth: width, align: CellAlign.Right, skin: CellSkin.KeyCap);
    }

    /// <summary>The link was clicked: the Server tab (one place in the back/forward history).</summary>
    internal bool PressServerSettings()
    {
        if (!ServerSettingsLinkShown) return false;
        try { OpenServerSettings?.Invoke(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Install Server settings link threw: " + e.Message);
            return false;
        }

        return true;
    }

    /// <summary>Readback <c>serverSettingsLink {shown, target, landing, label}</c> (the shape of the other link rows).</summary>
    internal Dictionary<string, object?> ServerSettingsLinkToWidget()
    {
        var d = SettingsLinks.Readback(SettingsLinks.TargetServerTab, SettingsLinks.LandingTop, InstallSimpleCopy.ServerSettingsLink);
        d["shown"] = ServerSettingsLinkShown;
        d["actsOnServer"] = IsSimple && SimpleActsOnServer;
        return d;
    }

    /// <summary>Simple's Version column.</summary>
    public const float SimpleVersionWidth = 96f;

    /// <summary>Simple's status words (<c>↑ Update available</c>, <c>Removed at next start</c> on one line).</summary>
    public const float SimpleStatusWidth = 200f;

    /// <summary>Simple's one button: wide enough for its longest caption, so every row's button lines up.</summary>
    public static readonly float SimpleButtonWidth = Math.Max(
        InstallColumns.SideButtonWidth(SideCells.CancelUninstallCaption),
        Math.Max(InstallColumns.SideButtonWidth(SideCells.CancelInstallCaption), InstallColumns.SideButtonWidth(SideCells.UninstallCaption)));

    // ---- The view and where it is remembered --------------------------------------------------

    /// <summary>
    /// Reads the remembered view (<c>[Mods] InstallView</c>). Null (tests, an older wiring): no view
    /// switch and the Advanced screen, exactly as before.
    /// </summary>
    internal Func<string?>? ViewProbe { get; set; }

    /// <summary>Writes the view the player picked (<see cref="InstallViewSetting.ValueOf"/>).</summary>
    internal Action<string>? ViewSink { get; set; }

    private InstallView? _viewRead;

    /// <summary>The view on show. Read from the setting once per build; the switch sets it at once.</summary>
    internal InstallView View
    {
        get
        {
            var probe = ViewProbe;
            if (probe == null) return InstallView.Advanced;
            if (_viewRead is { } known) return known;
            string? stored;
            try { stored = probe(); }
            catch { stored = null; }
            var view = InstallViewSetting.Parse(stored);
            _viewRead = view;
            return view;
        }
    }

    internal bool IsSimple => View == InstallView.Simple;

    /// <summary>The switch is drawn whenever the view is remembered somewhere.</summary>
    internal bool ViewSwitchShown => ViewProbe != null;

    /// <summary>
    /// The view switch was pressed: remember the view and rebuild the tab in it. An open confirmation
    /// is answered No first (it belongs to a row of the other view).
    /// </summary>
    public bool SetView(InstallView view)
    {
        if (ViewProbe == null) return false;
        bool changed = View != view;
        _viewRead = view;
        try { ViewSink?.Invoke(InstallViewSetting.ValueOf(view)); }
        catch { /* the view still changes for this session */ }
        if (!changed) return true;
        if (_state.Prompt != InstallPromptKind.None) PromptNo();
        InvalidateSideCache();
        _sideMemo?.Clear();
        RequestDetailRebuild();
        return true;
    }

    /// <summary>Readback <c>view {mode, stored, switchShown}</c>.</summary>
    internal Dictionary<string, object?> ViewToWidget()
    {
        string? stored = null;
        try { stored = ViewProbe?.Invoke(); }
        catch { stored = null; }
        var d = new Dictionary<string, object?>
        {
            ["mode"] = InstallViewSetting.Name(View),
            ["stored"] = stored,
            ["switchShown"] = ViewSwitchShown,
            ["actsOnServer"] = IsSimple && SimpleActsOnServer,
            ["value"] = stored,
        };
        // SIMPLESCOPE1: what Simple acts on, and why.
        var scope = SimpleScope;
        d["simpleScope"] = scope.Scope;
        d["simpleScopeReason"] = scope.Reason;
        // Settings organization stage 2: the toolbar switch mirrors [Mods] InstallView, at home on the Configuration tab.
        SettingPlaces.AddReadback(d, SettingPlaces.Id(ModsConfigKeys.Section, InstallViewSetting.ConfigKey), SettingPlaces.PluginId, keybind: false);
        return d;
    }

    // ---- Where Simple acts -------------------------------------------------------------------

    /// <summary>
    /// The server Simple points the per-side decision at: the connected server, else none (this PC
    /// only). The Advanced picker's choice is kept for Advanced and not used here.
    /// </summary>
    private static ServerPickEntry SimplePick(IReadOnlyList<ServerPickEntry> entries)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Kind == ServerPickKind.Connected) return entries[i];
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Kind == ServerPickKind.None) return entries[i];
        return new ServerPickEntry(ServerPicker.IdNone, ServerPickKind.None, ServerPicker.NoneCaption, "");
    }

    /// <summary>
    /// Does a Simple button act on the connected server too? Only when connected and the existing
    /// Server target's route to that server is open: the remote route (the server authorized this
    /// player: on its admin list or adminauthed, and allowed) or the local-folder route (the joined
    /// server is this PC's configured folder, which the Server target installs to without admin).
    /// Without a picker (tests, an older wiring) only the remote route proves a connection.
    /// </summary>
    internal bool SimpleActsOnServer
    {
        get
        {
            var route = CurrentRoute;
            var picked = PickedServer;
            if (picked != null)
                return picked.Kind == ServerPickKind.Connected && route.Kind is ServerRouteKind.Local or ServerRouteKind.Remote;
            return route.Kind == ServerRouteKind.Remote;
        }
    }

    /// <summary>
    /// SIMPLESCOPE1 (project rule, 2026-10-07): what Simple acts on, with the reason: <c>all</c> (this PC and the connected
    /// server, for its admin on the remote route, or the joined server is this PC's folder) or <c>client-only</c> (not
    /// connected, or the connected server's route is not open to this player: its words).
    /// </summary>
    internal (string Scope, string Reason) SimpleScope
    {
        get
        {
            var route = CurrentRoute;
            if (SimpleActsOnServer)
                return (ScopeAll, route.Kind == ServerRouteKind.Local ? "the connected server is this PC's server folder" : "an admin on the connected server");
            if (!ConnectedServerKnown) return (ScopeClientOnly, "not connected to a server");
            return (ScopeClientOnly, route.Block switch
            {
                ServerRouteBlock.NotAdmin => "not an admin on the connected server",
                ServerRouteBlock.NotAllowed => "an admin, but not allowed to install on the connected server",
                ServerRouteBlock.RemoteOff => "the connected server has remote installs switched off",
                ServerRouteBlock.NotProgenitor => "the connected server doesn't run Progenitor",
                ServerRouteBlock.Pending => "checking what the connected server allows",
                _ => "the connected server's route is not open",
            });
        }
    }

    internal const string ScopeAll = "all";
    internal const string ScopeClientOnly = "client-only";

    /// <summary>SIMPLESCOPE1: this client is joined to a server (the picker's Connected entry, else a route block only a connection gives).</summary>
    internal bool ConnectedServerKnown
    {
        get
        {
            var entries = PickerEntries();
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Kind == ServerPickKind.Connected) return true;
            var route = CurrentRoute;
            return route.Kind == ServerRouteKind.Remote
                || route.Block is ServerRouteBlock.NotAdmin or ServerRouteBlock.NotAllowed or ServerRouteBlock.RemoteOff
                    or ServerRouteBlock.NotProgenitor or ServerRouteBlock.Pending;
        }
    }

    /// <summary>
    /// SIMPLESCOPE1: the Simple drawer's note on a both-sides mod Simple acts on only for this PC while joined: the server
    /// half is the admins'. Empty otherwise.
    /// </summary>
    internal string SimpleServerHalfNote(CatalogEntry entry)
    {
        if (!IsSimple || entry == null || SideOf(entry) != PackageSide.Both || SimpleActsOnServer || !ConnectedServerKnown) return "";
        return InstallSimpleCopy.ServerHalfManaged(ServerIdentityFor(remote: true).ShortName);
    }

    // ---- Which listings Simple shows -----------------------------------------------------------

    private HashSet<string>? _dependedOn;

    /// <summary>A library or dependency package (<see cref="InstallLibraries"/>): Advanced only.</summary>
    internal bool IsLibrary(CatalogEntry entry)
    {
        if (entry == null) return false;
        if (_dependedOn == null)
        {
            IReadOnlyList<CatalogEntry>? all = null;
            try { all = _catalog.Search(null); }
            catch { all = null; }
            _dependedOn = InstallLibraries.DependedOn(all ?? Array.Empty<CatalogEntry>());
        }

        return InstallLibraries.IsLibrary(entry, _dependedOn);
    }

    /// <summary>
    /// Simple lists only listings for the current game update (when it is known), like Advanced's
    /// default <c>Current game update</c> filter, except a mod with a copy on a side Simple acts on,
    /// which stays listed so it can be updated or removed.
    /// </summary>
    internal const bool SimpleCurrentUpdateOnly = true;

    /// <summary>The Simple row of one listing (hidden ones carry their reason).</summary>
    internal InstallSimpleRow SimpleRowFor(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (IsTool(entry)) return InstallSimpleRow.Hidden(InstallSimpleRules.HiddenTool);
        if (IsLoader(entry)) return InstallSimpleRow.Hidden(InstallSimpleRules.HiddenLoader);
        if (IsLibrary(entry)) return InstallSimpleRow.Hidden(InstallSimpleRules.HiddenLibrary);
        var row = InstallSimpleRules.Decide(SideCellsCached(entry), SideOf(entry), SimpleActsOnServer);
        if (row.Listed && SimpleCurrentUpdateOnly && !row.HasCopy && CurrentUpdateTag is { Length: > 0 } tag
            && !CatalogUpdateTags.IsTagged(entry, tag))
            return InstallSimpleRow.Hidden(InstallSimpleRules.HiddenOtherUpdate);
        return row;
    }

    /// <summary>Listed in Simple at all (the chips' and the count's baseline).</summary>
    internal bool SimpleListed(CatalogEntry entry)
    {
        if (entry == null || IsHidden(entry)) return false;
        try { return SimpleRowFor(entry).Listed; }
        catch { return false; }
    }

    /// <summary>Shown in Simple now: listed, matching the search and the Show chip.</summary>
    internal bool SimpleVisible(CatalogEntry entry)
    {
        if (entry == null || IsHidden(entry)) return false;
        if (SearchRank(entry, _search, ReadmeTextFor(entry)) <= 0) return false;
        InstallSimpleRow row;
        try { row = SimpleRowFor(entry); }
        catch { return false; }
        return row.Listed && SimpleShowMatches(row, _state.SimpleShow);
    }

    internal static bool SimpleShowMatches(InstallSimpleRow row, InstallSimpleShow show) => show switch
    {
        InstallSimpleShow.Installed => row.HasCopy,
        InstallSimpleShow.Updates => row.NeedsUpdate,
        _ => true,
    };

    /// <summary>Simple's Show choice (per catalog for the process, like Advanced's filters).</summary>
    internal InstallSimpleShow SimpleShow => _state.SimpleShow;

    public void SetSimpleShow(InstallSimpleShow show)
    {
        if (_state.SimpleShow == show) return;
        _state.SimpleShow = show;
        RefilterListings();
    }

    /// <summary>The Show chips' counts: listings each choice would show with the search as it is.</summary>
    internal IReadOnlyDictionary<InstallSimpleShow, int> SimpleCounts()
    {
        var counts = new Dictionary<InstallSimpleShow, int>
        {
            [InstallSimpleShow.All] = 0,
            [InstallSimpleShow.Installed] = 0,
            [InstallSimpleShow.Updates] = 0,
        };
        var entries = AllEntries();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry == null || IsHidden(entry) || SearchRank(entry, _search, ReadmeTextFor(entry)) <= 0) continue;
            InstallSimpleRow row;
            try { row = SimpleRowFor(entry); }
            catch { continue; }
            if (!row.Listed) continue;
            counts[InstallSimpleShow.All]++;
            if (row.HasCopy) counts[InstallSimpleShow.Installed]++;
            if (row.NeedsUpdate) counts[InstallSimpleShow.Updates]++;
        }

        _lastSimpleCounts = counts;
        return counts;
    }

    private IReadOnlyDictionary<InstallSimpleShow, int>? _lastSimpleCounts;

    // ---- Sort ------------------------------------------------------------------------------------

    /// <summary>
    /// Simple's order: Advanced's sorts for the columns Simple shows — Mod (A to Z), Released (newest
    /// first, the default) and Downloads (most first); undated or uncounted listings last either way.
    /// </summary>
    internal List<CatalogEntry> SimpleSorted(IReadOnlyList<CatalogEntry> entries) =>
        Sorted(entries, _state.SimpleSort, _state.SimpleSortReversed, null);

    /// <summary>
    /// A click on a sortable Simple header, as in Advanced: a new column sorts in its natural direction,
    /// the sorted column reverses. Only Mod, Released and Downloads sort here.
    /// </summary>
    public void SimpleHeaderSort(string? key)
    {
        if (!TryParseSortKey(key, out var sort) || sort is not (InstallSort.Name or InstallSort.Released or InstallSort.Downloads)) return;
        if (_state.SimpleSort == sort) _state.SimpleSortReversed = !_state.SimpleSortReversed;
        else
        {
            _state.SimpleSort = sort;
            _state.SimpleSortReversed = false;
        }
        MarkHeaderSorted();
        RequestDetailRebuild();
    }

    internal string SimpleSortName => SortKeyOf(_state.SimpleSort);

    internal string SimpleSortDirection => InstallColumns.DirectionName(_state.SimpleSort, _state.SimpleSortReversed);

    // ---- Cells -----------------------------------------------------------------------------------

    /// <summary>The view switch: two chips, the current view on the selected gold face.</summary>
    internal List<CellSpec> ViewSwitchCells()
    {
        bool simple = IsSimple;
        return new List<CellSpec>
        {
            Chip(CellViewSimple, InstallSimpleCopy.ViewSimple, simple),
            Chip(CellViewAdvanced, InstallSimpleCopy.ViewAdvanced, !simple),
        };
    }

    /// <summary>The Mod cell's two lines: the name over <c>one-liner · owner</c>.</summary>
    internal static string SimpleNameText(CatalogEntry entry)
    {
        string name = HeadingText(entry);
        string snippet = ListingSnippet(entry.Description);
        string owner = HeaderAuthor(entry);
        string second = snippet.Length > 0 && owner.Length > 0 ? snippet + SideCells.Dot + owner
            : snippet.Length > 0 ? snippet
            : owner.Length > 0 ? InstallModsCopy.ByPrefix + owner
            : "";
        return second.Length == 0 ? name : name + "\n" + second;
    }

    /// <summary>
    /// Simple's cell keys in order: expand, icon, Mod, Released, Downloads, Version, the status words and
    /// the one button. Released stays left of the right-aligned group; Downloads, Version, the status and
    /// the button pack from the right edge in that order, so the words sit immediately left of the button.
    /// </summary>
    public static readonly IReadOnlyList<string> SimpleKeys = new[]
    {
        CellExpand, CellIcon, CellName, CellShare, CellReleased, CellDownloads, CellVersion, CellInstallStatus, CellInstall,
    };

    /// <summary>One Simple row (see <see cref="SimpleKeys"/>). Released and Downloads are Advanced's own cells.</summary>
    internal List<CellSpec> SimpleRowCells(CatalogEntry entry)
    {
        var simple = SimpleRowFor(entry);
        bool open = string.Equals(_state.DrawerKey, entry.Key, StringComparison.OrdinalIgnoreCase);
        var expand = new CellSpec(CellKind.Button, CellExpand).With(
            text: open ? InstallModsCopy.GlyphCollapse : InstallModsCopy.GlyphExpand,
            skin: CellSkin.Expander,
            align: CellAlign.Center);
        var name = new CellSpec(CellKind.Text, CellName).With(text: SimpleNameText(entry), wrap: CellWrap.TwoLine);
        name.NoteLead = UpdateNoteText(entry, CurrentUpdateTag);
        name.NoteLeadTone = CellTone.Warn;
        var cells = new List<CellSpec>
        {
            expand,
            new CellSpec(CellKind.Image, CellIcon).With(skin: CellSkin.IconWell),
            name,
            ShareRowCell(entry),
            new CellSpec(CellKind.Muted, CellReleased).With(text: ReleasedShortText(entry)),
            new CellSpec(CellKind.Muted, CellDownloads).With(text: InstallColumns.CompactCount(entry.Downloads), tone: CellTone.Muted),
            new CellSpec(CellKind.Muted, CellVersion).With(text: HeaderVersion(entry), tone: CellTone.Muted,
                width: CellWidth.Px(SimpleVersionWidth), minWidth: SimpleVersionWidth, align: CellAlign.Right),
            SimpleStatusCell(simple),
            SimpleButtonCell(simple),
        };
        ShapeSimple(cells);
        return cells;
    }

    /// <summary>Advanced's column widths and narrow-pane drop order (Released, then Downloads; never the sorted one).</summary>
    private void ShapeSimple(List<CellSpec> cells)
    {
        string sorted = InstallColumns.CellOfSort(_state.SimpleSort);
        for (int i = 0; i < cells.Count; i++) InstallColumns.Shape(cells[i], sorted);
    }

    private static CellSpec SimpleStatusCell(InstallSimpleRow simple)
    {
        var text = new CellSpec(CellKind.Muted, CellInstallStatus).With(
            text: InstallColumns.StatusLines(simple.Words, SimpleStatusWidth - GlyphRoom(simple.Glyph), InstallColumns.SideTextFontSize),
            tone: SideTone(simple.Tone),
            width: CellWidth.Px(SimpleStatusWidth),
            minWidth: SimpleStatusWidth,
            fontSize: InstallColumns.SideTextFontSize,
            align: CellAlign.Right);
        text.Glyph = simple.Glyph;
        return text;
    }

    private static CellSpec SimpleButtonCell(InstallSimpleRow simple)
    {
        string caption = InstallSimpleRules.Caption(simple.Button);
        bool shown = caption.Length > 0;
        var spec = new CellSpec(shown ? CellKind.Button : CellKind.Text, CellInstall).With(
            text: caption,
            width: CellWidth.Px(SimpleButtonWidth),
            minWidth: SimpleButtonWidth,
            fontSize: InstallColumns.SideButtonFontSize,
            skin: CellSkin.PopupButton,
            tone: SideButtonTone(simple.Button),
            align: CellAlign.Right);
        spec.Visible = shown;
        return spec;
    }

    /// <summary>
    /// Simple's header: Mod (A to Z), Released (newest first), Downloads (most first), Version, and Status
    /// over the words and the button. Captions, widths and sort marks as in Advanced.
    /// </summary>
    internal List<CellSpec> SimpleHeaderCells()
    {
        float status = SimpleStatusWidth + CellLayout.Gap + SimpleButtonWidth;
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellExpand).With(text: "", align: CellAlign.Center),
            new CellSpec(CellKind.Text, CellIcon).With(text: ""),
            new CellSpec(CellKind.Text, CellName).With(text: InstallModsCopy.HeaderMod, sortKey: SortKeyName),
            new CellSpec(CellKind.Text, CellReleased).With(text: InstallModsCopy.HeaderReleased, sortKey: SortKeyReleased),
            new CellSpec(CellKind.Text, CellDownloads).With(text: InstallModsCopy.HeaderDownloads, sortKey: SortKeyDownloads),
            new CellSpec(CellKind.Text, CellVersion).With(text: InstallSimpleCopy.HeaderVersion,
                width: CellWidth.Px(SimpleVersionWidth), minWidth: SimpleVersionWidth, align: CellAlign.Right),
            new CellSpec(CellKind.Text, CellInstallStatus).With(text: InstallSimpleCopy.HeaderStatus,
                width: CellWidth.Px(status), minWidth: status, align: CellAlign.Right),
        };
        ShapeSimple(cells);
        foreach (var cell in cells)
        {
            cell.SmallCaps = true;
            cell.FontSizeOverride = InstallColumns.HeaderFontSize;
        }

        return cells;
    }

    /// <summary>Simple's one filter row: the Show caption and its three chips with their counts.</summary>
    internal List<CellSpec> SimpleFilterCells()
    {
        var counts = SimpleCounts();
        return new List<CellSpec>
        {
            Caption(CellShowCaption, InstallModsCopy.ShowCaption),
            SimpleChip(CellShowAll, InstallModsCopy.ShowAll, InstallSimpleShow.All, counts),
            SimpleChip(CellShowInstalled, InstallModsCopy.ShowInstalled, InstallSimpleShow.Installed, counts),
            SimpleChip(CellShowUpdate, InstallSimpleCopy.ShowUpdates, InstallSimpleShow.Updates, counts),
        };
    }

    private CellSpec SimpleChip(string key, string caption, InstallSimpleShow show, IReadOnlyDictionary<InstallSimpleShow, int> counts)
    {
        int count = counts.TryGetValue(show, out int n) ? n : 0;
        var spec = Chip(key, InstallModsCopy.ShowChipCaption(caption, count), _state.SimpleShow == show,
            width: ChipNaturalWidth(InstallModsCopy.ShowChipCaption(caption, 999)));
        spec.Tone = SimpleChipTone(_state.SimpleShow == show, show, count);
        return spec;
    }

    /// <summary>Selected: the gold face; <c>Updates</c> with matches: the amber attention rim.</summary>
    internal static CellTone SimpleChipTone(bool on, InstallSimpleShow show, int count) =>
        on ? CellTone.ChipOn
        : count > 0 && show == InstallSimpleShow.Updates ? CellTone.ChipAttention
        : CellTone.ChipOff;

    /// <summary>Build Simple's filter row and wire its chips (the keys Advanced uses for the same choices).</summary>
    private void BuildSimpleFilters(IUiPanel panel)
    {
        var cells = SimpleFilterCells();
        var row = panel.CellRow(new CellRowSpec(FiltersKey, cells));
        _filterRows.Add(row);
        _filters = row;
        foreach (var cell in cells) _filterRowOf[cell.Key] = row;
        OnFilterClick(CellShowAll, () => SetSimpleShow(InstallSimpleShow.All));
        OnFilterClick(CellShowInstalled, () => SetSimpleShow(InstallSimpleShow.Installed));
        OnFilterClick(CellShowUpdate, () => SetSimpleShow(InstallSimpleShow.Updates));
    }

    private void SyncSimpleChips()
    {
        var counts = SimpleCounts();
        SyncSimpleChip(CellShowAll, InstallModsCopy.ShowAll, InstallSimpleShow.All, counts);
        SyncSimpleChip(CellShowInstalled, InstallModsCopy.ShowInstalled, InstallSimpleShow.Installed, counts);
        SyncSimpleChip(CellShowUpdate, InstallSimpleCopy.ShowUpdates, InstallSimpleShow.Updates, counts);
    }

    private void SyncSimpleChip(string key, string caption, InstallSimpleShow show, IReadOnlyDictionary<InstallSimpleShow, int> counts)
    {
        int count = counts.TryGetValue(show, out int n) ? n : 0;
        SetFilterCell(key, text: InstallModsCopy.ShowChipCaption(caption, count), tone: SimpleChipTone(_state.SimpleShow == show, show, count));
    }

    /// <summary>Rewrite one Simple row's status and button in place (a button cell cannot appear here; the rebuild adds it).</summary>
    private void SyncSimpleCells(IUiCellRow row, CatalogEntry entry)
    {
        var simple = SimpleRowFor(entry);
        try
        {
            row.SetCell(CellInstallStatus,
                text: InstallColumns.StatusLines(simple.Words, SimpleStatusWidth - GlyphRoom(simple.Glyph), InstallColumns.SideTextFontSize),
                tone: SideTone(simple.Tone));
        }
        catch { /* */ }
        string caption = InstallSimpleRules.Caption(simple.Button);
        try { row.SetCell(CellInstall, text: caption, visible: caption.Length > 0 && IsButtonCell(row, CellInstall)); }
        catch { /* */ }
    }

    /// <summary>Does the built Simple row have the button cell its decision now needs?</summary>
    private bool SimpleButtonFits(IUiCellRow row, CatalogEntry entry) =>
        SimpleRowFor(entry).Button == SideButton.None || IsButtonCell(row, CellInstall);

    // ---- The one button ------------------------------------------------------------------------

    /// <summary>The Simple install box acts on both sides already: no second "on both" yes.</summary>
    private bool _promptSimpleBoth;

    /// <summary>
    /// A Simple row's button: the side flows the Advanced buttons run. One side: that side's button.
    /// Both sides: Install / Update is the row's existing <c>Install on both</c> / <c>Update both</c>
    /// flow (one confirmation naming both targets); Uninstall, Cancel install and Cancel uninstall
    /// are the whole-row confirmations that name every half they reach.
    /// </summary>
    public bool OnSimpleClick(string key)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key, out var entry) || entry == null) return false;
        if (PromptOpenFor(entry)) return false;
        _sideMemo?.Remove(entry.Key);
        _sideCache.Remove(entry.Key);
        var simple = SimpleRowFor(entry);
        var button = simple.Button;
        if (button == SideButton.None) return false;
        switch (simple.Targets)
        {
            case InstallSimpleTargets.Client:
                return OnSideClick(key, server: false, button);
            case InstallSimpleTargets.Server:
                return OnSideClick(key, server: true, button);
            case InstallSimpleTargets.Both:
                break;
            default:
                return false;
        }

        switch (button)
        {
            case SideButton.Install:
            case SideButton.Update:
            {
                if (SideCellsFor(entry).Shortcut != button) return false;
                if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
                _promptSimpleBoth = true;
                bool began = OnBothClick(key);
                if (!began && _state.Prompt == InstallPromptKind.None) _promptSimpleBoth = false;
                return began;
            }

            case SideButton.Uninstall:
                return OpenWholePrompt(entry, InstallPromptKind.Uninstall);
            case SideButton.CancelInstall:
                return OpenWholePrompt(entry, InstallPromptKind.CancelInstall);
            case SideButton.CancelUninstall:
                return OpenWholePrompt(entry, InstallPromptKind.Reactivate);
        }

        return false;
    }

    private bool OpenWholePrompt(CatalogEntry entry, InstallPromptKind kind)
    {
        if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
        _promptSide = PromptSide.Whole;
        return OpenPrompt(entry, kind);
    }

    /// <summary>The open install box came from a Simple both-sides button.</summary>
    internal bool PromptIsSimpleBoth => _promptSimpleBoth && _state.Prompt != InstallPromptKind.None;

    // ---- Readback ------------------------------------------------------------------------------

    /// <summary>
    /// Readback <c>listing.simple {kind, status, statusText, glyph, tone, button, buttonName, targets[],
    /// acted[], listed, hidden}</c>.
    /// </summary>
    internal Dictionary<string, object?> SimpleToWidget(CatalogEntry entry)
    {
        var simple = SimpleRowFor(entry);
        return new Dictionary<string, object?>
        {
            ["kind"] = simple.Kind,
            ["status"] = simple.Words,
            ["statusText"] = simple.StatusText,
            ["glyph"] = simple.Glyph,
            ["tone"] = SideCells.Name(simple.Tone),
            ["button"] = InstallSimpleRules.Caption(simple.Button),
            ["buttonName"] = SideCells.Name(simple.Button),
            ["targets"] = InstallViewSetting.TargetsList(simple.Targets),
            ["acted"] = InstallViewSetting.TargetsList(simple.Acted),
            ["listed"] = simple.Listed,
            ["hidden"] = simple.HiddenReason,
            // SIMPLESCOPE1: the drawer's note on a both-sides mod Simple acts on only for this PC.
            ["serverHalfNote"] = SimpleServerHalfNote(entry),
        };
    }

    /// <summary>Readback of Simple's filter row: <c>filters {view, show, counts {all, installed, update}, rows}</c>.</summary>
    internal Dictionary<string, object?> SimpleFiltersToWidget()
    {
        var counts = _lastSimpleCounts ?? SimpleCounts();
        int Count(InstallSimpleShow show) => counts.TryGetValue(show, out int n) ? n : 0;
        return new Dictionary<string, object?>
        {
            ["view"] = InstallViewSetting.Name(View),
            ["show"] = InstallViewSetting.ShowName(_state.SimpleShow),
            ["counts"] = new Dictionary<string, object?>
            {
                ["all"] = Count(InstallSimpleShow.All),
                ["installed"] = Count(InstallSimpleShow.Installed),
                ["update"] = Count(InstallSimpleShow.Updates),
            },
            ["atDefaults"] = AtDefaults,
            ["rows"] = _filterRows.ConvertAll(r => r.Key),
        };
    }

    /// <summary>Readback <c>header.columns</c> in Simple.</summary>
    internal Dictionary<string, object?> SimpleColumnsToWidget()
    {
        float rowWidth = ColumnRowWidth();
        var cells = SimpleHeaderCells();
        var placed = InstallColumns.Resolve(rowWidth, cells);
        var widths = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < cells.Count; i++)
            widths[cells[i].Key] = placed[i].Visible ? (object)Math.Round(placed[i].Width, 1) : 0d;
        return new Dictionary<string, object?>
        {
            ["view"] = "simple",
            ["set"] = new[] { "mod", "released", "downloads", "version", "status" },
            ["keys"] = SimpleKeys,
            ["rowWidth"] = Math.Round(rowWidth, 1),
            ["widths"] = widths,
            ["dropped"] = InstallColumns.DroppedAt(rowWidth, cells),
            ["dropPriority"] = InstallColumns.DropPriority,
            ["sortable"] = new[] { SortKeyName, SortKeyReleased, SortKeyDownloads },
            ["sort"] = SimpleSortName,
            ["sortDirection"] = SimpleSortDirection,
        };
    }

    /// <summary>A short readback count, for logs and tests.</summary>
    internal string SimpleCountsText()
    {
        var c = SimpleCounts();
        return c[InstallSimpleShow.All].ToString(CultureInfo.InvariantCulture) + "/"
            + c[InstallSimpleShow.Installed].ToString(CultureInfo.InvariantCulture) + "/"
            + c[InstallSimpleShow.Updates].ToString(CultureInfo.InvariantCulture);
    }
}
