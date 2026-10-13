using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

// Settings scopes, step 4 (docs/settings-scopes.md, decided 2026-10-03): the mod page's
// Settings in the two-section model, for the settings a client already knows — its own cfg rows
// (Personal, client-owned) and the synced rows ConfigSync knows (Server-wide). Server-owned Personal
// values and settings of mods this client lacks need the settings manifest (step 5).

/// <summary>
/// What the page knows about the joined server, read when the page is built and again on each
/// re-evaluation (a handshake change, a commit). Plain values, so every page state is testable.
/// </summary>
internal sealed class SettingsScopeContext
{
    public SettingConnection Connection { get; init; } = SettingConnection.NotConnected;

    /// <summary>This mod's server part on the joined server (the existing handshake evidence).</summary>
    public ServerPartState ServerPart { get; init; } = ServerPartState.Unknown;

    /// <summary>
    /// The server authorized this player for server-wide edits (its remote-install status: admin list or
    /// adminauth). True when the server sent no status (an older Core): the edit control is drawn and the
    /// server decides, as before.
    /// </summary>
    public bool CanEditServerWide { get; init; } = true;

    /// <summary>The joined server's name when known.</summary>
    public string ServerName { get; init; } = "";

    /// <summary>The joined server's <c>address:port</c> when known.</summary>
    public string ServerAddress { get; init; } = "";

    /// <summary><c>Applies to everyone on …</c> under Server settings.</summary>
    public string ServerLine => SettingScopeSections.ServerLine(Connection != SettingConnection.NotConnected, ServerName, ServerAddress);

    /// <summary>What changes the control a row is drawn with (admin or not): a change rebuilds the page.</summary>
    public string BuildKey => (Connection == SettingConnection.Progenitor ? "p" : "-") + (CanEditServerWide ? "a" : "-");

    /// <summary>
    /// The context a view built with only a server-part reader assumes (the raw settings pane, tests that
    /// predate scopes): a joined Progenitor server with that evidence, edits left to the server.
    /// </summary>
    public static SettingsScopeContext FromServerPart(ServerPartState part) => new()
    {
        Connection = SettingConnection.Progenitor,
        ServerPart = part,
    };

    /// <summary>The connection from today's facts: connected, the handshake result, and whether the server's request arrived.</summary>
    public static SettingConnection ConnectionOf(bool connected, bool handshakeOk, bool serverRequestSeen) =>
        !connected ? SettingConnection.NotConnected
        : handshakeOk ? SettingConnection.Progenitor
        : serverRequestSeen ? SettingConnection.Waiting
        : SettingConnection.NotProgenitor;

    public Dictionary<string, object?> Readback() => new()
    {
        ["connection"] = Connection switch
        {
            SettingConnection.NotConnected => "not-connected",
            SettingConnection.Waiting => "waiting",
            SettingConnection.NotProgenitor => "not-progenitor",
            _ => "progenitor",
        },
        ["serverPart"] = ModFrameworkLine.NameOf(ServerPart),
        ["canEditServerWide"] = CanEditServerWide,
        ["serverLine"] = ServerLine,
    };
}

/// <summary>One scope section as the view draws it: its heading, its line and its author subsections.</summary>
internal sealed class ScopeDrawGroup
{
    public ScopeDrawGroup(SettingScope scope) => Scope = scope;

    public SettingScope Scope { get; }

    public string Heading => Scope == SettingScope.ServerWide ? SettingScopeSections.ServerHeading : SettingScopeSections.YourHeading;

    public List<ModSettingsSection> Sections { get; } = new();
}

internal static class ModSettingsScopes
{
    /// <summary>The scope section heading's readback key, and its cell row key prefix.</summary>
    internal const string RowScopeSection = "page-scope-section";
    internal const string ScopeKeyPrefix = "page-scope-";
    internal const string ScopeLineKeyPrefix = "page-scope-line-";
    internal const string CellScopeLine = "line";

    /// <summary>
    /// The scope an item belongs to. A setting row is Server-wide when its entry is synced (the server's
    /// value is in force); a builder button or info line when the author declared <c>scope: Server</c>;
    /// everything else — the player's own cfg, keybinds, registered actions — is Personal. A row declared
    /// <c>scope: Server</c> on an entry that is not synced stays Personal, as it stays local today.
    /// </summary>
    internal static SettingScope ScopeOf(ModSettingsItem item)
    {
        if (item == null) return SettingScope.Personal;
        if (item.Row is { } row)
        {
            bool synced;
            try { synced = row.IsSynced || row.IsSyncedNow; }
            catch { synced = false; }
            return synced ? SettingScope.ServerWide : SettingScope.Personal;
        }

        return item.Options.Scope == SettingScope.ServerWide ? SettingScope.ServerWide : SettingScope.Personal;
    }

    /// <summary>
    /// Split the page's sections into <b>Your settings</b> then <b>Server settings</b>. Each author section
    /// becomes a subsection of the scope its items belong to (a section with both is split, keeping the
    /// item order); a title that only restates the scope (<see cref="SettingScopeSections.IsRedundant"/>)
    /// draws no subheading. A divider goes with the scope of the item before it. A subsection holding
    /// only dividers is dropped, and so is an empty scope.
    /// </summary>
    internal static List<ScopeDrawGroup> Regroup(IReadOnlyList<ModSettingsSection>? sections)
    {
        var personal = new ScopeDrawGroup(SettingScope.Personal);
        var server = new ScopeDrawGroup(SettingScope.ServerWide);
        if (sections != null)
        {
            foreach (var section in sections)
            {
                if (section == null) continue;
                var mine = new List<ModSettingsItem>();
                var theirs = new List<ModSettingsItem>();
                var last = SettingScope.Personal;
                foreach (var item in section.Items)
                {
                    if (item == null) continue;
                    var scope = item.Kind == ModSettingsItemKind.Divider ? last : ScopeOf(item);
                    last = scope;
                    (scope == SettingScope.ServerWide ? theirs : mine).Add(item);
                }

                Add(personal, section, mine, SettingScope.Personal);
                Add(server, section, theirs, SettingScope.ServerWide);
            }
        }

        var groups = new List<ScopeDrawGroup>(2);
        if (personal.Sections.Count > 0) groups.Add(personal);
        if (server.Sections.Count > 0) groups.Add(server);
        return groups;
    }

    private static void Add(ScopeDrawGroup group, ModSettingsSection source, List<ModSettingsItem> items, SettingScope scope)
    {
        bool meaningful = false;
        foreach (var item in items) meaningful |= item.Kind != ModSettingsItemKind.Divider;
        if (!meaningful) return;
        string title = SettingScopeSections.IsRedundant(source.Title) ? "" : source.Title;
        group.Sections.Add(new ModSettingsSection(title, source.Kind, items) { Scope = scope, SourceTitle = source.Title });
    }

    /// <summary>
    /// The availability facts of one item. Personal items are client-owned (step 4 knows no server-owned
    /// Personal value yet); Server-wide items are the server's, and today's evidence stands in for the
    /// settings manifest: a ConfigSync lock proves the server runs the mod at a compatible version and
    /// sent its value; on a joined Progenitor server with no lock, a server part the handshake named
    /// means the versions differ (the snapshot skipped it), and one it did not name is absent.
    /// </summary>
    internal static SettingAvailabilityFacts FactsOf(
        ModSettingsItem item, SettingScope scope, SettingsScopeContext context, bool shownByIf, bool enabledByIf)
    {
        var part = context.ServerPart;
        var partEvidence = part == ServerPartState.Running ? SettingEvidence.Yes
            : part == ServerPartState.NotOnServer ? SettingEvidence.No
            : SettingEvidence.Unknown;
        var facts = new SettingAvailabilityFacts
        {
            Scope = scope,
            Owner = scope == SettingScope.ServerWide ? SettingOwner.Server : SettingOwner.Client,
            Connection = context.Connection,
            ServerSharesSettings = context.Connection == SettingConnection.Progenitor ? SettingEvidence.Yes : SettingEvidence.Unknown,
            ManifestArrived = context.Connection == SettingConnection.Progenitor,
            ServerPart = partEvidence,
            Compatible = true,
            CanEditServerWide = context.CanEditServerWide || !OptedIn(item),
            NeedsServerPart = item.Options.NeedsServerPart,
            AuthorEnabled = enabledByIf,
            AuthorVisible = shownByIf,
            WhenAlone = item.Options.WhenAlone,
        };

        if (scope == SettingScope.ServerWide && item.Row is { } row)
        {
            bool locked;
            try { locked = ConfigSync.IsLocked(row.PluginId, row.Section, row.Key); }
            catch { locked = false; }
            if (locked) facts = facts with { ServerPart = SettingEvidence.Yes, Compatible = true };
            else if (partEvidence == SettingEvidence.Yes) facts = facts with { Compatible = false };
        }

        return facts;
    }

    /// <summary>
    /// A Server-wide row an admin could edit: opted into administrator edits. A row that is not is
    /// read-only for everyone, admins included (decision 2), so it is never <c>Admin only</c>.
    /// </summary>
    internal static bool OptedIn(ModSettingsItem item)
    {
        if (item?.Row is not { } row) return false;
        try { return ServerSettings.IsEditable(row.RowId); }
        catch { return false; }
    }

    /// <summary>The grayed reason a row's help line carries, with the author's fallback for an absent half.</summary>
    internal static string WhyOf(SettingAvailabilityResult result, ModSettingOptions options)
    {
        if (result.State != SettingRowState.Grayed) return result.State == SettingRowState.AdminOnly ? result.Reason : "";
        string fallback = options?.Fallback ?? "";
        return SettingAvailability.MeansAlone(result.Code) && fallback.Length > 0
            ? result.Reason + " " + ModSettingsLayout.Sentence(fallback)
            : result.Reason;
    }

    /// <summary>The scope section heading: the game's section header (its title size, middle-aligned; the runtime draws the red rule).</summary>
    internal static CellRowSpec HeadingRow(int index, string title) =>
        new(ScopeKeyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new List<CellSpec>
            {
                // MODPAGE2: at the page margin.
                ModPageSpacing.Lead(),
                new CellSpec(CellKind.Text, ModSettingsView.CellTitle).With(
                    text: title ?? "", width: CellWidth.Fraction(1f),
                    fontSize: ModSettingsRowGrid.HeaderFontSize, vAlign: CellVAlign.Middle),
            },
            height: ModSettingsRowGrid.HeaderHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true);

    /// <summary>The one muted line under Server settings: <c>Applies to everyone on …</c>.</summary>
    internal static CellRowSpec LineRow(int index, string line) =>
        new(ScopeLineKeyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new List<CellSpec>
            {
                ModPageSpacing.Lead(),
                new CellSpec(CellKind.Muted, CellScopeLine).With(
                    text: line ?? "", width: CellWidth.Fraction(1f), fontSize: GameSettingsLook.ActionRowText, tone: CellTone.Muted),
            },
            height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true);

    /// <summary>
    /// An author subsection's heading, the same in both scopes: small caps in the game's gold, a step larger than the rows'
    /// help (settings organization review, live 2026-10-05: the muted 15-point titles under Server settings were too faint to
    /// find; Players joining, Server list, Server rules, Gameplay and Operator read as headings now).
    /// </summary>
    internal static CellRowSpec SubheadingRow(int index, string title, bool openKeybinds)
    {
        var spec = ModSettingsView.SectionRow(index, title, openKeybinds);
        foreach (var cell in spec.Cells)
        {
            if (cell.Key != ModSettingsView.CellTitle) continue;
            cell.SmallCaps = true;
            cell.Tone = SubheadingTone;
            cell.FontSizeOverride = SubheadingFontSize;
        }

        return spec;
    }

    /// <summary>Three homes: a drawer header's cells — the count and the marker.</summary>
    internal const string CellDrawerCount = "count";
    internal const string CellDrawerChevron = "chevron";

    /// <summary>The drawer header's cell-row key prefix: <c>drawer-&lt;group slug&gt;</c> (<c>widget_click label=drawer-operator</c>).</summary>
    internal const string DrawerKeyPrefix = "drawer-";

    /// <summary>
    /// Three homes (live polish 2026-10-06: the far-right dot did not read as a fold): a drawer's header. From the left: the
    /// fold marker — the game's own dropdown arrow (<see cref="CellSkin.Expander"/>; pointing down open, right closed) in the
    /// subheading's gold at the title's size, or the kit's glyph when the arrow was not borrowed, never a glyph the game font
    /// lacks — then the gold small-caps title, then the muted count as <c>(12)</c>. The whole row is a click target with the
    /// game's list-row hover tint; the Keys group keeps its <b>Open in Keybinds</b> at the right.
    /// </summary>
    internal static CellRowSpec DrawerRow(string title, int count, bool openKeybinds, string? group = null)
    {
        var cells = new List<CellSpec>(6)
        {
            DrawerChevronCell(open: true),
            SubheadingTitleCell(title),
            new CellSpec(CellKind.Muted, CellDrawerCount).With(
                text: DrawerCountText(count), fontSize: ModSettingsView.SectionFontSize, vAlign: CellVAlign.Bottom, tone: CellTone.Muted),
            new CellSpec(CellKind.Spacer, "gap"),
        };
        if (openKeybinds)
            cells.Add(new CellSpec(CellKind.Button, ModSettingsView.CellOpenKeybinds).With(
                text: ModSettingsView.OpenKeybindsCaption, align: CellAlign.Right, skin: CellSkin.KeyCap));
        return new CellRowSpec(
            DrawerKeyPrefix + SectionNav.Slug(group ?? title), cells,
            height: ModSettingsView.SectionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, soft: true, quietHover: true);
    }

    /// <summary>
    /// Live 2026-10-06 (the drawer header drew <c>ION (5)</c> after a value change): whether a drawer header's fold marker
    /// and its whole title are inside the row as placed. <paramref name="cells"/> are the drawn cells' left edges and widths
    /// in the row's own coordinates (the same frame as <paramref name="rowLeft"/>). <c>chevronShown</c>: the marker cell is
    /// drawn, has a width, and starts inside the row. <c>titleShown</c>: the title cell starts inside the row, ends inside it,
    /// and is at least as wide as its full text in small caps. Null when the cell was not drawn at all.
    /// </summary>
    internal static Dictionary<string, object?> DrawerHeadingFit(
        float rowLeft, float rowWidth, IReadOnlyList<(string Key, float Left, float Width)> cells, string title)
    {
        const float slack = 1f;
        float rowRight = rowLeft + rowWidth;
        float need = DrawerTitleNeeds(title);
        (string Key, float Left, float Width)? chevron = null, text = null;
        foreach (var c in cells ?? Array.Empty<(string, float, float)>())
        {
            if (c.Key == CellDrawerChevron) chevron = c;
            else if (c.Key == ModSettingsView.CellTitle) text = c;
        }

        bool chevronShown = chevron is { } ch && ch.Width >= 1f && ch.Left >= rowLeft - slack && ch.Left + ch.Width <= rowRight + slack;
        bool titleShown = text is { } t && t.Left >= rowLeft - slack && t.Left + t.Width <= rowRight + slack && t.Width + slack >= need;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chevronShown"] = chevronShown,
            ["titleShown"] = titleShown,
            ["chevronLeft"] = chevron is { } c1 ? Math.Round(c1.Left - rowLeft, 1) : null,
            ["titleLeft"] = text is { } t1 ? Math.Round(t1.Left - rowLeft, 1) : null,
            ["titleWidth"] = text is { } t2 ? Math.Round(t2.Width, 1) : null,
            ["titleNeeds"] = Math.Round(need, 1),
        };
    }

    /// <summary>The width a drawer title's full text takes in small caps (the estimate <see cref="SubheadingTitleCell"/> sizes it from, without its spare room).</summary>
    internal static float DrawerTitleNeeds(string title) => CellLayout.EstimateTextWidth(title ?? "", SubheadingFontSize) * 1.25f;

    /// <summary>A drawer's count as drawn: muted, in parentheses.</summary>
    internal static string DrawerCountText(int count) => "(" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    /// <summary>The fold marker's width: the title's size and a little room.</summary>
    internal const float DrawerChevronWidth = SubheadingFontSize + 10f;

    /// <summary>
    /// The fold marker: an expander button cell (the game's arrow), gold, at the title's size. Its caption is the kit's
    /// fallback glyph, which the runtime replaces with the arrow when it was borrowed.
    /// </summary>
    internal static CellSpec DrawerChevronCell(bool open) =>
        new CellSpec(CellKind.Button, CellDrawerChevron).With(
            text: open ? InstallModsCopy.GlyphCollapse : InstallModsCopy.GlyphExpand, skin: CellSkin.Expander,
            align: CellAlign.Center, vAlign: CellVAlign.Bottom, tone: SubheadingTone, fontSize: SubheadingFontSize,
            width: CellWidth.Px(DrawerChevronWidth), minWidth: DrawerChevronWidth);

    /// <summary>
    /// Every group title on the three homes, drawer or plain subheading: gold, small caps, the subheading's size, one style
    /// (live 2026-10-06: the UI subheading read lighter than the others).
    /// </summary>
    internal static CellSpec SubheadingTitleCell(string title, bool fill = false)
    {
        // An auto width is only an estimate of the caption (it cut short titles live, ModSettingsView.SectionRow): the
        // title gets its estimate with room to spare, small caps being wider than the estimate's lower case.
        float titleWidth = CellLayout.EstimateTextWidth(title ?? "", SubheadingFontSize) * 1.25f + 16f;
        var cell = new CellSpec(CellKind.Text, ModSettingsView.CellTitle).With(
            text: title ?? "", width: fill ? CellWidth.Fill(titleWidth) : CellWidth.Px(titleWidth), minWidth: titleWidth,
            fontSize: SubheadingFontSize, vAlign: CellVAlign.Bottom, tone: SubheadingTone);
        cell.SmallCaps = true;
        return cell;
    }

    /// <summary>
    /// Three homes: a plain group subheading (four settings or fewer) in the drawers' style, its title set in by the fold
    /// marker's width so every group title on a page lines up. Not a list row: nothing lights under the pointer.
    /// </summary>
    internal static CellRowSpec PlainSubheadingRow(int index, string title, bool openKeybinds)
    {
        var cells = new List<CellSpec>(4)
        {
            new CellSpec(CellKind.Muted, "lead").With(text: "", width: CellWidth.Px(DrawerChevronWidth), minWidth: DrawerChevronWidth),
            SubheadingTitleCell(title, fill: !openKeybinds),
        };
        if (openKeybinds)
        {
            cells.Add(new CellSpec(CellKind.Spacer, "gap"));
            cells.Add(new CellSpec(CellKind.Button, ModSettingsView.CellOpenKeybinds).With(
                text: ModSettingsView.OpenKeybindsCaption, align: CellAlign.Right, skin: CellSkin.KeyCap));
        }

        return new CellRowSpec(
            ModSettingsView.SectionKeyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture), cells,
            height: ModSettingsView.SectionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true);
    }

    internal const CellTone SubheadingTone = CellTone.Gold;

    internal const float SubheadingFontSize = 18f;
}
