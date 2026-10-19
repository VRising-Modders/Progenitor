using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>What the Server tab can show for the joined server (three homes).</summary>
internal enum ServerTabState
{
    /// <summary>No server joined: the explanation, the groups as muted lines, and the last server (SERVEREMPTY1).</summary>
    NotConnected = 0,

    /// <summary>Joined, the Progenitor handshake has not answered yet.</summary>
    Waiting = 1,

    /// <summary>Joined a server without Progenitor: its settings cannot be read or changed here.</summary>
    NoProgenitor = 2,

    /// <summary>Joined a Progenitor server: the header and the groups.</summary>
    Ready = 3,
}

/// <summary>
/// Three homes (docs/settings-organization.md, "Three homes (2026-10-06)"): the <b>Server</b> tab — how the joined server is
/// set up. Its title is the server's name (the game's section header with the red rule), a line says who may edit, then
/// the groups of Progenitor's page registration that <see cref="ProgenitorPage.ServerTabTitles"/> names, drawn with the mod
/// page's row kit (<see cref="ModSettingsView"/>, embedded), so every server-side rule, the lock list and the audit are
/// the ones those rows always had. Not connected, on a server without Progenitor, or while the handshake answers, it shows an
/// explanation and the groups as muted lines instead; not connected, also the last server joined with <b>Join</b>
/// (SERVEREMPTY1). Game-free.
/// </summary>
internal sealed class ServerTabPane
{
    internal const string Title = "Server";

    /// <summary>The words after the server's name on the header line (approved wording 2026-10-06).</summary>
    internal const string EditRule = "admins edit, others read only";

    internal const string EditRuleSuffix = " · " + EditRule;

    internal const string HeaderLine = "How this server is set up" + EditRuleSuffix;

    /// <summary>The not-connected explanation (user design, 2026-10-06).</summary>
    internal const string NotConnectedLine =
        "You're not on a server. When you join one that runs Progenitor, its setup shows here: admins edit it, everyone else can read it.";

    internal const string NoProgenitorLine =
        "This server doesn't run Progenitor, so its setup can't be shown or changed here. On a server that runs it, admins edit it and everyone else can read it.";

    internal const string WaitingLine = "Waiting for the server to answer… Its setup shows here once it does.";

    /// <summary>The Last server row's lead word, and its button.</summary>
    internal const string LastServerLead = "Last server:";

    internal const string JoinCaption = "Join";

    /// <summary>
    /// SERVEREMPTY1: what each group holds, for the empty states' muted list (the group's name, then these words). Kept to
    /// what the group draws today (docs/mods-menu.md, "The Server tab").
    /// </summary>
    internal static readonly IReadOnlyList<(string Title, string Holds)> GroupSummaries = new[]
    {
        (ProgenitorPage.WhenPlayersJoinTitle, "message, resets, join screens"),
        (ProgenitorPage.ModsPlayersNeedTitle, "version matching, required and approved mods, mid-session loads"),
        (ProgenitorPage.ServerListTitle, "name, description, listing"),
        (ProgenitorPage.ServerRulesTitle, "mode, clan size, password"),
        (ProgenitorPage.ChatServerTitle, "links, word filter, Discord relay"),
        (ProgenitorPage.PlayersWithoutProgenitorTitle, "message, seconds before disconnect"),
        (ProgenitorPage.UpdatesTitle, "the server's mod updates, restarts, backups"),
        (ProgenitorPage.OperatorTitle, "remote installs, who may install"),
    };

    /// <summary>The description column's text with nothing hovered.</summary>
    internal const string ColumnIntro = "Point at a setting to read what it does and who may change it.";

    /// <summary>Readback keys: the header row, and the empty state's line.</summary>
    internal const string RowHead = "server-tab-head";
    internal const string RowEmpty = "server-tab-empty";

    /// <summary>
    /// SERVEREMPTY1 readback: on the empty state's line, <c>serverEmpty {state, lastServer {name, address} | null, joinShown,
    /// joinEnabled, groups}</c>; on each group line <c>serverEmptyGroup {group, slug, holds}</c>; on the Last server row
    /// <c>serverLast {name, address, needsPassword, joinShown, joinEnabled, state, lastPress}</c>.
    /// </summary>
    internal const string RowServerEmpty = "serverEmpty";
    internal const string RowServerEmptyGroup = "serverEmptyGroup";
    internal const string RowServerLast = "serverLast";

    /// <summary>Cell-row keys: a group line is <c>server-empty/&lt;slug&gt;</c>; the Last server row <c>server-last</c> with its <c>join</c> button.</summary>
    internal const string GroupKeyPrefix = "server-empty/";
    internal const string LastServerKey = "server-last";
    internal const string CellGroupName = "name";
    internal const string CellGroupHolds = "holds";
    internal const string CellLastLead = "lead";
    internal const string CellLastName = "server";
    internal const string CellJoin = "join";

    /// <summary>The name column of a group line (canvas units): room for "Players without Progenitor".</summary>
    internal const float GroupNameWidth = 300f;

    internal Func<Progenitor.Install.ServerRoute>? RouteProbe { get; set; }

    private readonly Func<SettingsScopeContext> _context;
    private readonly Func<LastServerFacts> _lastServer;
    private readonly Func<LastServerRecord, string>? _join;
    private LastServerFacts _builtLast = new();
    private string _lastPress = "";
    private readonly List<(IUiHandle Handle, string Key, object? Value)> _readback = new();
    private ModSettingsView? _view;
    private ServerTabState _builtState;
    private string _builtTitle = "";

    internal ServerTabPane(Func<SettingsScopeContext> context, Func<LastServerFacts>? lastServer = null, Func<LastServerRecord, string>? join = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _lastServer = lastServer ?? (() => new LastServerFacts());
        _join = join;
    }

    /// <summary>The Last server row's state: drawn only when not connected, then as the facts say.</summary>
    internal static LastServerJoinState LastServerStateOf(ServerTabState state, LastServerFacts? facts) =>
        state != ServerTabState.NotConnected || facts == null ? LastServerJoinState.Hidden : facts.JoinState;

    /// <summary>The Last server row's name cell: <c>Night Garden · 1.2.3.4:9876</c> (the address alone when it is the name).</summary>
    internal static string LastServerText(LastServerRecord record)
    {
        string name = record.DisplayName;
        return string.Equals(name, record.Endpoint, StringComparison.OrdinalIgnoreCase) ? name : name + " · " + record.Endpoint;
    }

    /// <summary>A group line of the empty states: the group's name in gold, what it holds muted.</summary>
    internal static CellRowSpec GroupRow(string title, string holds) => new(
        GroupKeyPrefix + SectionNav.Slug(ProgenitorPage.NavCaption(title)),
        new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellGroupName).With(
                text: ProgenitorPage.NavCaption(title), width: CellWidth.Px(GroupNameWidth), tone: CellTone.Gold,
                fontSize: GameSettingsLook.ActionRowText, vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis),
            new CellSpec(CellKind.Muted, CellGroupHolds).With(
                text: holds ?? "", width: CellWidth.Fill(120f), tone: CellTone.Muted,
                fontSize: GameSettingsLook.ActionRowText, vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis),
        },
        height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: ModSettingsRowGrid.NameInset);

    /// <summary>
    /// The Last server row: <c>Last server:</c>, the name and address, and <b>Join</b> (the settings rows' key-cap button,
    /// grayed while a join is in progress), laid out as the settings link rows are.
    /// </summary>
    internal static CellRowSpec LastServerRow(LastServerRecord record, bool joinEnabled) => new(
        LastServerKey,
        new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellLastLead).With(
                text: LastServerLead, tone: CellTone.Muted, fontSize: GameSettingsLook.NameText, vAlign: CellVAlign.Middle),
            new CellSpec(CellKind.Text, CellLastName).With(
                text: LastServerText(record), width: CellWidth.Fill(160f), fontSize: GameSettingsLook.NameText,
                vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis),
            new CellSpec(CellKind.Button, CellJoin).With(
                text: JoinCaption, align: CellAlign.Right, skin: CellSkin.KeyCap, tone: joinEnabled ? CellTone.Normal : CellTone.Disabled),
        },
        height: ModSettingsRowGrid.RowPitch, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: ModSettingsRowGrid.NameInset);

    /// <summary>The state from the joined server's facts (the same context the mod pages' scope sections use).</summary>
    internal static ServerTabState StateOf(SettingsScopeContext? context) => (context?.Connection ?? SettingConnection.NotConnected) switch
    {
        SettingConnection.Progenitor => ServerTabState.Ready,
        SettingConnection.Waiting => ServerTabState.Waiting,
        SettingConnection.NotProgenitor => ServerTabState.NoProgenitor,
        _ => ServerTabState.NotConnected,
    };

    /// <summary>
    /// The pinned line under the server's name: connection, the route's management words
    /// (<see cref="Progenitor.Install.ServerRoute.ManagementLabel"/>: a blocked route names its reason) and access.
    /// </summary>
    internal static string StatusLine(ServerTabState state, Progenitor.Install.ServerRoute route, bool canEdit) =>
        state == ServerTabState.NotConnected ? "Disconnected"
            : "Connected" + (route.ManagementLabel is { Length: > 0 } management ? " · " + management : "")
                + (state == ServerTabState.Ready ? (canEdit ? " · Admin access" : " · Read-only access") : " · " + EmptyLine(state));

    /// <summary>The empty state's one line, or empty when the tab shows its groups.</summary>
    internal static string EmptyLine(ServerTabState state) => state switch
    {
        ServerTabState.NotConnected => NotConnectedLine,
        ServerTabState.Waiting => WaitingLine,
        ServerTabState.NoProgenitor => NoProgenitorLine,
        _ => "",
    };

    /// <summary>The tab's title: the joined server's name, or <b>Server</b> when it has none (never its address).</summary>
    internal static string TitleOf(SettingsScopeContext? context)
    {
        if (context == null || context.Connection == SettingConnection.NotConnected) return Title;
        string name = (context.ServerName ?? "").Trim();
        return name.Length == 0 ? Title : name;
    }

    /// <summary>The groups the left list jumps to: the Server tab's groups when it draws them, else none.</summary>
    internal static IReadOnlyList<string> NavGroupsFor(ServerTabState state) =>
        state != ServerTabState.Ready ? Array.Empty<string>() : AllNavGroups();

    /// <summary>
    /// SERVEREMPTY1: the groups the left list shows dimmed (no highlight, no jump) when the tab draws none: every group, so
    /// the tab never reads as half built. Empty when it draws them (they are the live left list then).
    /// </summary>
    internal static IReadOnlyList<string> DimmedNavGroupsFor(ServerTabState state) =>
        state == ServerTabState.Ready ? Array.Empty<string>() : AllNavGroups();

    private static List<string> AllNavGroups()
    {
        var list = new List<string>(ProgenitorPage.ServerTabTitles.Count);
        foreach (string title in ProgenitorPage.ServerTabTitles) list.Add(ProgenitorPage.NavCaption(title));
        return list;
    }

    internal ServerTabState State => _builtState;

    internal ModSettingsView? SettingsView => _view;

    internal IUiHandle? TitleHeading { get; private set; }

    /// <summary>The heading of a group the left list names (its caption or its title), or null.</summary>
    internal IUiHandle? GroupHeading(string? group) => _view?.HeadingFor(group);

    /// <summary>
    /// True when the joined server's answer changed what the tab draws: another state or title (join, leave, the handshake),
    /// or a row's control (admin or not, the lock list) — the owner rebuilds. A grayed reason alone changes in place.
    /// </summary>
    internal bool NeedsRebuild()
    {
        SettingsScopeContext now;
        try { now = _context(); }
        catch { return false; }
        if (StateOf(now) != _builtState) return true;
        if (!string.Equals(TitleOf(now), _builtTitle, StringComparison.Ordinal)) return true;
        if (_builtState == ServerTabState.NotConnected
            && !string.Equals(LastKey(SafeLastServer()), LastKey(_builtLast), StringComparison.Ordinal)) return true;
        return _view?.NeedsRebuildForScope() == true;
    }

    /// <summary>
    /// Draw the tab into <paramref name="panel"/>: the title with its red rule, then either the empty state's line or the
    /// header line and the groups (<paramref name="sections"/>, read when called). <paramref name="configure"/> lets the
    /// owner set the view's callbacks before it draws.
    /// </summary>
    internal void Build(IUiPanel panel, Func<List<ModSettingsSection>> sections, Action<ModSettingsView>? configure = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        Release();
        SettingsScopeContext context;
        try { context = _context(); }
        catch { context = new SettingsScopeContext(); }
        _builtState = StateOf(context);
        _builtTitle = TitleOf(context);
        panel.ContentAlignment = ConfigurationLayout.Alignment;
        Ui.HidePaneTitle(panel);
        Ui.PinMenuRows(panel, MenuRowPin.Top);
        var title = panel.CellRow(new CellRowSpec(RowHead, new[]
        {
            new CellSpec(CellKind.Text, "name").With(text: _builtTitle, width: CellWidth.Fill(200f),
                fontSize: 26f, wrap: CellWrap.Wrap),
        }, height: 64f, plain: true));
        TitleHeading = title;
        var state = _builtState;
        AddReadback(title, RowHead, (Func<object?>)(() => Describe(state)));
        var route = RouteProbe?.Invoke() ?? default;
        string status = StatusLine(state, route, context.CanEditServerWide);
        var statusRow = panel.Note(status);
        AddReadback(statusRow, "server-context", status);
        Ui.PinMenuRows(panel, MenuRowPin.Content);
        panel.SettingsDescriptionMode(true);

        if (state != ServerTabState.Ready)
        {
            BuildEmpty(panel, state);
            return;
        }

        var header = panel.Note(HeaderLine);
        AddReadback(header, RowHead + "-line", HeaderLine);
        BuildProgenitorLink(panel);
        var view = new ModSettingsView(SettingsPanel.PluginId, sections(), null, null, _context)
        {
            Embedded = true,
            OmitServerChip = true,
            EmptyHeading = "Setting help",
            DefaultToFirstSetting = false,
            EmptyBody = ColumnIntro,
        };
        configure?.Invoke(view);
        _view = view;
        try { view.Build(panel, AddReadback); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the Server tab's settings threw: " + e.Message); }
    }

    /// <summary>
    /// SERVERLINK1 (requested 2026-10-07: "at the top, add a link to the progenitor MOD server side settings"): under the
    /// header line, a text link to Progenitor's page at its On this server group, where Progenitor's own server toggles are
    /// (Photo mode, Ally-safe hits; admins only). A player who sees none of them gets <b>Progenitor's page »</b> and a note
    /// instead, and lands on the page's top. Back returns here (the jump is a place in the history).
    /// </summary>
    private void BuildProgenitorLink(IUiPanel panel)
    {
        bool atGroup = ProgenitorPage.OnThisServerSettingsShown();
        _linkAtGroup = atGroup;
        string caption = atGroup ? SettingsLinks.ProgenitorServerText : SettingsLinks.ProgenitorPageText;
        var link = panel.CellRow(SettingsLinks.TextLink(SettingsLinks.ProgenitorServerKey, caption, atGroup ? null : SettingsLinks.ProgenitorPageNote));
        link.OnClick(SettingsLinks.CellOpen, () => PressProgenitorLink());
        AddReadback(link, ConfigurationPane.RowPageLink, (Func<object?>)(() => DescribeProgenitorLink()));
    }

    private bool _linkAtGroup;
    private string _lastLinkLanding = "";

    /// <summary>The link's click (and <c>click_at row=link-progenitor-server/open</c>): the landing, or empty when the menu is closed.</summary>
    internal string PressProgenitorLink()
    {
        string landing;
        try { landing = ModsMenu.OpenProgenitorServerSettingsFromLink(_linkAtGroup); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: the Server tab's Progenitor link threw: " + e.Message);
            landing = "";
        }

        _lastLinkLanding = landing;
        return landing;
    }

    /// <summary>Readback <c>page-link {target, landing, label, note, lastLanding}</c> (the shape of the other link rows).</summary>
    internal Dictionary<string, object?> DescribeProgenitorLink()
    {
        var d = SettingsLinks.Readback(SettingsLinks.TargetProgenitorPage,
            _linkAtGroup ? SettingsLinks.LandingOnThisServer : SettingsLinks.LandingTop,
            _linkAtGroup ? SettingsLinks.ProgenitorServerText : SettingsLinks.ProgenitorPageText);
        d["note"] = _linkAtGroup ? null : SettingsLinks.ProgenitorPageNote;
        d["skin"] = UiCellRowHandle.SkinName(CellSkin.Link);
        d["lastLanding"] = _lastLinkLanding.Length > 0 ? _lastLinkLanding : null;
        return d;
    }

    /// <summary>
    /// SERVEREMPTY1: an empty state (not connected, a server without Progenitor, waiting): its explanation, one muted line per
    /// group (its name and what it holds), and, not connected, the last server this client joined with <b>Join</b>.
    /// </summary>
    private void BuildEmpty(IUiPanel panel, ServerTabState state)
    {
        _builtLast = state == ServerTabState.NotConnected ? SafeLastServer() : new LastServerFacts();
        string text = EmptyLine(state);
        var line = panel.Label(text);
        AddReadback(line, RowEmpty, new Dictionary<string, object?> { ["state"] = NameOf(state), ["line"] = text });
        AddReadback(line, RowServerEmpty, (Func<object?>)(() => DescribeEmpty(state)));
        panel.ShowDescription(ModSettingsDescription.Empty(Title, text));

        foreach (var (title, holds) in GroupSummaries)
        {
            var row = panel.CellRow(GroupRow(title, holds));
            string caption = ProgenitorPage.NavCaption(title);
            AddReadback(row, RowServerEmptyGroup, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["group"] = caption,
                ["slug"] = SectionNav.Slug(caption),
                ["holds"] = holds,
            });
        }

        var last = _builtLast;
        var joinState = LastServerStateOf(state, last);
        if (joinState == LastServerJoinState.Hidden || last.Record == null) return;
        var lastRow = panel.CellRow(LastServerRow(last.Record, joinState == LastServerJoinState.Ready));
        lastRow.OnClick(CellJoin, () => PressJoin());
        AddReadback(lastRow, RowServerLast, (Func<object?>)(() => DescribeLast(state)));
    }

    /// <summary>
    /// <b>Join</b> on the Last server row (and <c>widget_click label=server-last/join</c>): connects as Join now does, unless
    /// the row is hidden or a join is already in progress (checked again now, not only when the row was drawn).
    /// </summary>
    internal string PressJoin()
    {
        var facts = SafeLastServer();
        var joinState = LastServerStateOf(_builtState, facts);
        string result;
        if (joinState == LastServerJoinState.Hidden || facts.Record == null) result = "hidden";
        else if (joinState == LastServerJoinState.Busy) result = "busy";
        else if (_join == null) result = "unavailable";
        else
        {
            try { result = _join(facts.Record) ?? ""; }
            catch (Exception e) { result = "threw: " + e.Message; }
        }

        _lastPress = result;
        Plugin.Logger?.LogInfo("Ui: the Server tab's Last server Join: " + result);
        return result;
    }

    private LastServerFacts SafeLastServer()
    {
        try { return _lastServer() ?? new LastServerFacts(); }
        catch { return new LastServerFacts(); }
    }

    private static string LastKey(LastServerFacts facts)
    {
        var r = facts.Record;
        return LastServerFacts.StateName(facts.JoinState) + "|" + (r == null ? "" : r.Endpoint + "|" + r.ServerName + "|" + r.NeedsPassword);
    }

    private static Dictionary<string, object?>? LastServerReadback(LastServerRecord? record) => record == null
        ? null
        : new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = record.DisplayName, ["address"] = record.Endpoint };

    private Dictionary<string, object?> DescribeEmpty(ServerTabState state)
    {
        var joinState = LastServerStateOf(state, _builtLast);
        var groups = new List<string>();
        foreach (var (title, _) in GroupSummaries) groups.Add(ProgenitorPage.NavCaption(title));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = NameOf(state),
            ["lastServer"] = joinState == LastServerJoinState.Hidden ? null : LastServerReadback(_builtLast.Record),
            ["joinShown"] = joinState != LastServerJoinState.Hidden,
            ["joinEnabled"] = joinState == LastServerJoinState.Ready,
            ["groups"] = groups,
        };
    }

    private Dictionary<string, object?> DescribeLast(ServerTabState state)
    {
        var record = _builtLast.Record;
        var joinState = LastServerStateOf(state, _builtLast);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = record?.DisplayName ?? "",
            ["address"] = record?.Endpoint ?? "",
            ["needsPassword"] = record?.NeedsPassword ?? false,
            ["joinShown"] = joinState != LastServerJoinState.Hidden,
            ["joinEnabled"] = joinState == LastServerJoinState.Ready,
            ["state"] = LastServerFacts.StateName(joinState),
            ["lastPress"] = _lastPress,
        };
    }

    private Dictionary<string, object?> Describe(ServerTabState state)
    {
        SettingsScopeContext context;
        try { context = _context(); }
        catch { context = new SettingsScopeContext(); }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = NameOf(state),
            ["title"] = _builtTitle,
            ["line"] = state == ServerTabState.Ready ? HeaderLine : EmptyLine(state),
            ["canEdit"] = state == ServerTabState.Ready && context.CanEditServerWide,
            ["groups"] = new List<string>(NavGroupsFor(state)),
            ["context"] = context.Readback(),
        };
    }

    internal static string NameOf(ServerTabState state) => state switch
    {
        ServerTabState.Waiting => "waiting",
        ServerTabState.NoProgenitor => "no-progenitor",
        ServerTabState.Ready => "ready",
        _ => "not-connected",
    };

    private void AddReadback(IUiHandle? handle, string key, object? value)
    {
        if (handle == null) return;
        _readback.Add((handle, key, value));
    }

    /// <summary>The <c>mods_ui widget_read</c> extras of a row this tab drew. False for anything else.</summary>
    internal bool TryDescribeRow(IUiHandle handle, out Dictionary<string, object?>? extra)
    {
        extra = null;
        if (handle == null) return false;
        foreach (var (h, key, value) in _readback)
        {
            if (!ReferenceEquals(h, handle)) continue;
            extra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            object? v = value;
            if (v is Func<object?> live)
            {
                try { v = live(); }
                catch (Exception e) { v = "error: " + e.Message; }
            }

            extra[key] = v;
        }

        return extra != null;
    }

    /// <summary>Close the view's pending requests and forget the rows (the panel disposes the widgets).</summary>
    internal void Release()
    {
        try { _view?.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the Server tab's release threw: " + e.Message); }
        _view = null;
        TitleHeading = null;
        _readback.Clear();
    }
}
