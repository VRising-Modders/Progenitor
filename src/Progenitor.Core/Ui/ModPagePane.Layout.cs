using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// MODPAGE2 (user feedback 2026-10-09: "the button locations need a revisit" and "letters have no padding on the margins";
/// layout approved live the same day). Every length of the mod page comes from the settings grid the Preferences rows use
/// (<see cref="ModSettingsRowGrid"/>) or the game's own Options numbers (<see cref="GameSettingsLook"/>). Game-free.
/// </summary>
internal static class ModPageSpacing
{
    /// <summary>The page margin: every text line starts this far in from the content's left edge, as a Preferences row's name does (the game's 16 units).</summary>
    internal static float Margin => ModSettingsRowGrid.NameInset;

    /// <summary>
    /// A cell row's caption draws this far right of its cell's left edge (measured live 2026-10-09 at 3840x1080: a first
    /// cell's caption 2 px right of a grid row's name at the same x), so the margin cell is that much narrower.
    /// </summary>
    internal const float CellTextOffset = 2f;

    /// <summary>The pitch of every details row: the game's 58-unit settings entry.</summary>
    internal static float RowPitch => ModSettingsRowGrid.RowPitch;

    /// <summary>A details row's drawn height: the pitch less the panel's gap between rows.</summary>
    internal static float FactRowHeight => RowPitch - PanelLayout.RowGap;

    /// <summary>A text value starts this far right of x1, where a dropdown bar's caption starts (the game's 10 units).</summary>
    internal static float ValueInset => GameSettingsLook.Kit(GameSettingsLook.DropdownCaptionLeft);

    /// <summary>Between buttons in one group: the kit's inline gap.</summary>
    internal const float ButtonGap = SettingsAlign.InlineGap;

    /// <summary>Space above a section heading (the existing Settings heading's), its size and its row height.</summary>
    internal const float HeadingGapAbove = SettingsAlign.HeadingExtraTop;

    internal const float HeadingFontSize = 24f;

    internal const float HeadingHeight = 40f;

    /// <summary>The About text's longest line: about 90 characters at the body size, never wider than the content.</summary>
    internal const float ReadableWidth = 820f;

    /// <summary>
    /// The actions bar's right inset: its band runs the content's full width (the scrollbar band included), so its last
    /// button ends one margin from the band's right edge, as the switch starts one margin from its left.
    /// </summary>
    internal static float ActionsRightInset => Margin;

    /// <summary>The by-line row's height.</summary>
    internal const float ByLineHeight = 28f;

    /// <summary>The width the details grid gets beside the description column (the column is always shown with it).</summary>
    internal static float FactsWidth(float contentWidth) => ModSettingsDescriptionLayout.RowsWidth(contentWidth);

    /// <summary>x1: the value/control column's left edge from the row's left (the grid's control column).</summary>
    internal static float X1(float factsWidth) => ModSettingsRowGrid.For(factsWidth).ControlLeft;

    /// <summary>The name cell's width: from its cell's left (the margin less the caption offset) to one gap before x1.</summary>
    internal static float NameWidth(float factsWidth) => Math.Max(40f, X1(factsWidth) - Margin + CellTextOffset - CellLayout.Gap);

    /// <summary>Where values end: the grid's bar edge (the game's 96 units in from the usable right).</summary>
    internal static float ValueRight(float factsWidth) => ModSettingsRowGrid.For(factsWidth).BarRight;

    /// <summary>The widest a text value's cell may be: from its cell's left (x1 + the caption inset, less the caption offset) to the bar edge.</summary>
    internal static float TextValueWidth(float factsWidth) =>
        Math.Max(40f, ValueRight(factsWidth) - (X1(factsWidth) + ValueInset - CellTextOffset));

    /// <summary>
    /// The page margin as a row's first cell: a cell row's first cell starts at the row's edge (<see cref="CellRowSpec.Indent"/>
    /// moves only a joined row's seam), so the margin is an empty cell one gap and the caption offset short of it.
    /// </summary>
    internal static CellSpec Lead()
    {
        float w = Math.Max(1f, Margin - CellLayout.Gap - CellTextOffset);
        return new CellSpec(CellKind.Muted, LeadKey).With(text: "", width: CellWidth.Px(w), minWidth: w);
    }

    internal const string LeadKey = "margin";

    /// <summary><paramref name="cells"/> with the margin cell first.</summary>
    internal static List<CellSpec> WithLead(IEnumerable<CellSpec> cells)
    {
        var list = new List<CellSpec> { Lead() };
        list.AddRange(cells);
        return list;
    }
}

/// <summary>
/// MODPAGE2: the description column beside the mod page's details grid. A details row describes itself while the pointer
/// is on it, the Updates dropdown's options describe themselves while hovered (OPTIONHOVER1), and the column falls back to
/// the page's own line (or the settings view's, when the page has settings). One per drawn page; the previous page's hover
/// subscriptions are released when the next one is drawn, and the page drawn last is let go when the page itself is
/// dropped (a workspace close, a switch away) or its plugin unloads, so its settings view (the plugin's keybind rows and
/// callbacks) cannot keep an unloaded plugin's load context alive.
/// </summary>
internal sealed class ModPageDetails
{
    internal const string ColumnIntro = "Point at a row to read what it is.";

    private static ModPageDetails? _active;
    private readonly IUiPanel _panel;
    private readonly string _heading;
    private readonly List<IDisposable> _leases = new();

    private ModPageDetails(IUiPanel panel, string heading, string pluginId)
    {
        _panel = panel;
        _heading = heading ?? "";
        PluginId = pluginId ?? "";
    }

    /// <summary>The plugin whose page this is (an id only, so the unload sweep can match it).</summary>
    internal string PluginId { get; }

    /// <summary>The page drawn last (its rows' hover writes the column).</summary>
    internal static ModPageDetails? Active => _active;

    /// <summary>The settings view drawn on the same page, whose own description is the rest state once it exists.</summary>
    internal ModSettingsView? View { get; set; }

    /// <summary>What the column says now (heading, body), for the readback.</summary>
    internal (string Heading, string Body) Shown { get; private set; }

    internal static ModPageDetails Begin(IUiPanel panel, string heading, string pluginId = "")
    {
        ReleaseActive();
        var details = new ModPageDetails(panel, heading, pluginId);
        _active = details;
        return details;
    }

    /// <summary>Let go of the page drawn last: its hover subscriptions and its settings view. Idempotent.</summary>
    internal static void ReleaseActive()
    {
        var old = _active;
        _active = null;
        old?.Release();
    }

    /// <summary>The unload sweep: let go of the page drawn last when it is <paramref name="pluginId"/>'s. True when it was.</summary>
    internal static bool ReleaseFor(string pluginId)
    {
        var active = _active;
        if (active == null || string.IsNullOrEmpty(pluginId)
            || !string.Equals(active.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return false;
        ReleaseActive();
        return true;
    }

    internal void Release()
    {
        foreach (var lease in _leases)
        {
            try { lease.Dispose(); }
            catch { /* the panel is gone */ }
        }

        _leases.Clear();
        // The view's rows hold the plugin's keybind entries and its server-part callback.
        View = null;
    }

    /// <summary>The column at rest: the settings view's own description, else the page's line.</summary>
    internal void ShowRest()
    {
        if (View != null)
        {
            try
            {
                View.ShowDescriptionAgain();
                return;
            }
            catch { /* the page's own line */ }
        }

        Show(_heading, ColumnIntro);
    }

    internal void Show(string heading, string body)
    {
        Shown = (heading ?? "", body ?? "");
        if (_panel is not UiPanelHandle) return;
        try { _panel.ShowDescription(new ModSettingsDescription("", heading ?? "", body ?? "", null, "", ModSettingsDescription.SourceHover)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod page description threw: " + e.Message); }
    }

    /// <summary>While the pointer is on <paramref name="row"/>, the column reads <paramref name="heading"/> and <paramref name="body"/>.</summary>
    internal void Describe(IUiHandle? row, string heading, string body)
    {
        if (row == null) return;
        try
        {
            _leases.Add(_panel.OnRowHover(row, entered =>
            {
                try { row.SetRowLit(entered); }
                catch { /* no grid chrome */ }
                if (entered) Show(heading, body);
                else ShowRest();
            }));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod page row description threw: " + e.Message); }
    }

    /// <summary>A choice's hovered option describes itself under <paramref name="heading"/>; leaving the options restores the row's text.</summary>
    internal void DescribeOptions(IUiHandle? control, string heading, string body)
    {
        if (control is not IUiOptionHover choice) return;
        try
        {
            _leases.Add(choice.OnOptionHover(index =>
            {
                var options = choice.Options;
                if (index >= 0 && index < options.Count)
                {
                    var o = options[index];
                    Show(heading, o.Label + ": " + (o.Description ?? ""));
                }
                else Show(heading, body);
            }));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod page option description threw: " + e.Message); }
    }
}

internal sealed partial class ModPagePane
{
    internal const string FactKeyPrefix = "page-fact-";

    /// <summary>A details row's name cell and value cell keys.</summary>
    internal const string CellFactName = "name";
    internal const string CellFactValue = "value";

    /// <summary>The readback key on each details row: <c>page-fact {name, description}</c>.</summary>
    internal const string RowFact = "page-fact";

    /// <summary>The Settings fact of a mod with no Progenitor settings (it replaces the empty Settings section).</summary>
    internal const string NoSettingsFact = "None here. Set it up the way its author describes.";

    internal const string DetailsHeading = "Details";
    internal const string AboutHeading = "About";

    // The details rows' names and what each says in the column.
    internal const string FactSides = "Installs on";
    internal const string FactClient = "Client";
    internal const string FactServer = "Server";
    internal const string FactReleased = "Released";
    internal const string FactStore = "Store page";
    internal const string FactPages = "Pages";
    internal const string FactSettings = "Settings";
    internal const string FactManaged = "Managed by";
    internal const string FactKept = "Kept versions";
    internal const string FactUpdate = "Update";

    internal const string SidesHelp = "Where this mod runs: on your PC, on the server, or on both.";
    internal const string ClientHelp = "This mod on your PC.";
    internal const string ServerHelp = "This mod on the server you are joined to or have picked in Install Mods.";
    internal const string ReleasedHelp = "When this version was published.";
    internal const string StoreHelp = "Copy a link to this mod's store page, or open the page.";
    internal const string PagesHelp = "This mod's own pages.";
    internal const string KeptHelp = "Other versions Progenitor keeps for this mod. Restore puts one back.";
    internal const string UpdateHelp = "A newer version is available. Open it in Install Mods to update.";

    /// <summary>A details row: the name at the margin, then the value cells from x1 (text values a caption inset further in).</summary>
    internal static CellRowSpec FactRow(string key, string name, IReadOnlyList<CellSpec> values, float factsWidth, bool textValue = true)
    {
        var cells = new List<CellSpec>(values.Count + 4)
        {
            ModPageSpacing.Lead(),
            new CellSpec(CellKind.Text, CellFactName).With(
                text: name, width: CellWidth.Px(ModPageSpacing.NameWidth(factsWidth)), minWidth: ModPageSpacing.NameWidth(factsWidth),
                fontSize: GameSettingsLook.NameText, vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis, tone: CellTone.SettingName),
        };
        // A text value's caption starts at x1 + ValueInset (its cell the caption offset before that).
        float lead = textValue ? ModPageSpacing.ValueInset - CellLayout.Gap - ModPageSpacing.CellTextOffset : 0f;
        if (lead > 0f) cells.Add(new CellSpec(CellKind.Muted, "inset").With(text: "", width: CellWidth.Px(lead), minWidth: lead));
        cells.AddRange(values);
        cells.Add(new CellSpec(CellKind.Spacer, "rest"));
        float right = Math.Max(0f, factsWidth - ModPageSpacing.ValueRight(factsWidth));
        return new CellRowSpec(FactKeyPrefix + key, cells, height: ModPageSpacing.FactRowHeight, rightInset: right,
            separator: true, skin: CellRowSkin.GameEntry, quietHover: true);
    }

    /// <summary>
    /// A text value sized to its caption. <paramref name="maxWidth"/> above zero caps the cell (C5: a long value then ends
    /// at the grid's bar edge with an ellipsis, see <see cref="ModPageSpacing.TextValueWidth"/>).
    /// </summary>
    internal static CellSpec FactText(string key, string text, CellTone tone = CellTone.Normal, float maxWidth = 0f)
    {
        var t = new CellSpec(CellKind.Text, key).With(text: text, tone: tone, fontSize: GameSettingsLook.NameText, vAlign: CellVAlign.Middle,
            wrap: CellWrap.Ellipsis, align: CellAlign.Left);
        float wAuto = CellLayout.EstimateTextWidth(text, GameSettingsLook.NameText) + 8f;
        if (maxWidth > 0f) wAuto = Math.Min(wAuto, maxWidth);
        t.Width = CellWidth.Px(wAuto);
        t.MinWidth = Math.Min(wAuto, 80f);
        return t;
    }

    internal static CellSpec FactButton(string key, string caption, CellTone tone = CellTone.Normal)
    {
        var b = new CellSpec(CellKind.Button, key).With(text: caption, tone: tone, align: CellAlign.Left);
        b.Width = CellWidth.Px(Math.Max(CellLayout.AutoWidth(b), HeadBlockLayout.ButtonWidth(caption)));
        return b;
    }

    /// <summary>
    /// The Store page row's buttons: <b>Store page</b> when a store url is known (live 2026-09-29: never a stray <c>»</c>),
    /// then <b>Share in chat</b> when the mod's Thunderstore package is known (chat stage 3). Each is sized to its caption.
    /// </summary>
    internal static List<CellSpec> StoreButtons(string? storeUrl, bool share)
    {
        var buttons = new List<CellSpec>(2);
        if (!string.IsNullOrWhiteSpace(storeUrl)) buttons.Add(FactButton(CellStore, ModPageCopy.StoreLinkCaption));
        if (share) buttons.Add(FactButton(CellShare, ModLinks.ShareCaption));
        return buttons;
    }

    /// <summary>The Pages row's buttons: the mod's custom pages in registration order, keyed <see cref="PageButtonKey"/>.</summary>
    internal static List<CellSpec> PageButtons(IReadOnlyList<(string Title, Action Open)>? pages)
    {
        var buttons = new List<CellSpec>(pages?.Count ?? 0);
        if (pages == null) return buttons;
        for (int i = 0; i < pages.Count; i++)
            if (!string.IsNullOrWhiteSpace(pages[i].Title)) buttons.Add(FactButton(PageButtonKey(i), pages[i].Title));
        return buttons;
    }

    /// <summary>A section heading (Details, About) at the page margin, with the game's category underline.</summary>
    internal static CellRowSpec SectionHeading(string key, string title) => new(
        "page-section-" + key,
        ModPageSpacing.WithLead(new[]
        {
            new CellSpec(CellKind.Text, "title").With(text: title, fontSize: ModPageSpacing.HeadingFontSize, vAlign: CellVAlign.Middle,
                width: CellWidth.Fill(120f)),
        }),
        height: ModPageSpacing.HeadingHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        skin: CellRowSkin.CategoryHeading, gapAbove: ModPageSpacing.HeadingGapAbove);

    /// <summary>The Help button's cell key in the actions bar.</summary>
    internal const string CellHelp = "help";

    /// <summary>Readback key of the Help button: <c>page-help {kind, caption, tooltip, cell, shown, source, file}</c>.</summary>
    internal const string RowHelp = "page-help";

    /// <summary>The Help button: a normal kit button sized to its caption, reporting the pointer for its tooltip.</summary>
    internal static CellSpec HelpButton()
    {
        var b = new CellSpec(CellKind.Button, CellHelp).With(text: ModPageCopy.HelpCaption, align: CellAlign.Right);
        b.Width = CellWidth.Px(Math.Max(CellLayout.AutoWidth(b), HeadBlockLayout.ButtonWidth(ModPageCopy.HelpCaption)));
        b.Hover = true;
        return b;
    }

    /// <summary>
    /// The actions bar: the switch, status word and version at the left; at the right, one margin in, <b>Help</b> when the
    /// mod ships a help file (<paramref name="help"/>) and then the action.
    /// </summary>
    internal static CellRowSpec ActionsBar(ModFrameworkLine line, bool help = false)
    {
        var bar = new List<CellSpec>(8) { ModPageSpacing.Lead() };
        if (line.SwitchVisible)
        {
            var sw = new CellSpec(CellKind.Switch, ModFrameworkLine.CellSwitch)
                .With(text: line.SwitchOn ? ModFrameworkLine.SwitchOnCaption : ModFrameworkLine.SwitchOffCaption,
                    tone: line.SwitchOn ? CellTone.Ok : CellTone.Muted);
            sw.Clickable = line.SwitchInteractive;
            sw.Width = CellWidth.Px(CellLayout.AutoWidth(sw));
            bar.Add(sw);
        }

        bar.Add(new CellSpec(CellKind.Text, ModFrameworkLine.CellStatusWord).With(
            text: line.StatusWord, tone: LabelTones.ToCellTone(line.StatusTone), fontSize: HeadBlockLayout.MetaFontSize));
        if (line.VersionText.Length > 0)
            bar.Add(new CellSpec(CellKind.Text, ModFrameworkLine.CellVersion).With(
                text: line.VersionText, tone: CellTone.Muted, fontSize: HeadBlockLayout.MetaFontSize));
        if (line.ConflictChip.Length > 0)
        {
            var chip = new CellSpec(CellKind.Badge, ModFrameworkLine.CellConflicts).With(text: line.ConflictChip, tone: CellTone.Warn);
            chip.Width = CellWidth.Px(CellLayout.AutoWidth(chip));
            bar.Add(chip);
        }

        bar.Add(new CellSpec(CellKind.Spacer, ModFrameworkLine.CellGap));
        if (help) bar.Add(HelpButton());
        if (line.ActionCaption is { Length: > 0 } action)
            bar.Add(new CellSpec(CellKind.Button, ModFrameworkLine.CellAction).With(
                text: action, align: CellAlign.Right,
                // SRVUNINST1: a disabled action keeps its place, dim.
                tone: line.ActionEnabled ? LabelTones.ToCellTone(line.ActionTone) : CellTone.Disabled));
        return new CellRowSpec(FrameworkKey, bar, height: ModFrameworkLine.BarHeight,
            rightInset: ModPageSpacing.ActionsRightInset, band: true);
    }

    /// <summary>The details grid's width for this page (beside the description column).</summary>
    internal float FactsWidth => ModPageSpacing.FactsWidth(RowWidth);

    /// <summary>The page's description column (what it says, for the readback), once built.</summary>
    internal ModPageDetails? Details { get; private set; }

    /// <summary>The Server details row (rewritten in place when the server's evidence changes), or null.</summary>
    internal IUiCellRow? ServerFactRow { get; private set; }

    /// <summary>One details row on the grid, described in the column.</summary>
    internal IUiCellRow AddFact(IUiPanel panel, string key, string name, IReadOnlyList<CellSpec> values, string help, bool textValue = true)
    {
        panel.SettingsGrid(true);
        IUiCellRow row;
        try { row = panel.CellRow(FactRow(key, name, values, FactsWidth, textValue)); }
        finally { panel.SettingsGrid(false); }
        Details?.Describe(row, name, help);
        Add(row, RowFact, new Dictionary<string, object?> { ["name"] = name, ["description"] = help });
        return row;
    }

    /// <summary>The title, or the mod's own header content in its place.</summary>
    private void BuildTitle(IUiPanel panel, ModPageActions actions)
    {
        if (actions.Header is { } custom)
        {
            // Review finding 3: the builder gets a panel that can only add rows. Its rows are recorded so a throw can be
            // rolled back, and an inline cluster it left open is closed.
            var restricted = new ModHeaderPanel(panel);
            try
            {
                custom(restricted);
                restricted.Finish();
                panel.ContentAlignment = BodyAlignment;
                HeaderKind = "custom";
                HeaderRows = restricted.Added.Count;
                return;
            }
            catch (Exception e)
            {
                int removed = restricted.RollBack();
                panel.ContentAlignment = BodyAlignment;
                Plugin.Logger?.LogWarning(
                    "Ui: mod header of " + _model.PluginId + " threw; removed " + removed
                    + " row(s) and drawing the default: " + e.Message);
                HeaderError = e.Message;
            }
        }

        HeaderKind = "default";
        var title = panel.CellRow(new CellRowSpec(HeadKey + "-title", ModPageSpacing.WithLead(new[]
        {
            new CellSpec(CellKind.Text, HeadBlockLayout.CellTitle).With(text: _model.Name, fontSize: HeadBlockLayout.TitleFontSize, width: CellWidth.Fill(200f),
                vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis),
        }), height: HeadBlockLayout.TitleRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
        TitleRow = title;
        string byLine = ModHeaderDefault.ByLine(_model.MetaAuthor, _model.OneLiner);
        if (byLine.Length > 0)
        {
            var by = panel.CellRow(new CellRowSpec(HeadKey + "-byline", ModPageSpacing.WithLead(new[]
            {
                new CellSpec(CellKind.Muted, "byline").With(text: byLine, fontSize: HeadBlockLayout.OneLinerFontSize, width: CellWidth.Fill(200f),
                    vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis),
            }), height: ModPageSpacing.ByLineHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
            Add(by, RowOneLiner, byLine);
        }
    }

    /// <summary>The default title row, or null under a custom header.</summary>
    internal IUiCellRow? TitleRow { get; private set; }

    /// <summary>The actions bar (the framework line), once built.</summary>
    private void BuildActionsBar(IUiPanel panel, ModPageActions actions)
    {
        var line = Framework;
        bool help = actions.Help != null && actions.HelpTarget.Found;
        var row = panel.CellRow(ActionsBar(line, help));
        FrameworkRow = row;
        if (help)
        {
            row.OnClick(CellHelp, actions.Help!);
            string tip = ModPageCopy.HelpTooltip(_model.Name);
            row.OnCellHover(CellHelp, entered => HelpHover(row, entered, tip));
        }

        var helpTarget = actions.HelpTarget;
        Add(row, RowHelp, new Dictionary<string, object?>
        {
            ["kind"] = "button",
            ["caption"] = ModPageCopy.HelpCaption,
            ["tooltip"] = ModPageCopy.HelpTooltip(_model.Name),
            ["cell"] = CellHelp,
            ["shown"] = help,
            ["source"] = help ? helpTarget.SourceName : "none",
            ["file"] = help ? helpTarget.Relative : "",
        });
        // SRVUNINST1: a disabled action takes no click; the page says why under the header.
        if (actions.Action != null && line.ActionCaption != null && line.ActionEnabled) row.OnClick(ModFrameworkLine.CellAction, actions.Action);
        // Stage 3: the switch takes the click only while it can act.
        if (actions.Switch != null && line.SwitchInteractive) row.OnClick(ModFrameworkLine.CellSwitch, actions.Switch);

        Add(row, RowFramework, (Func<object?>)(() => Framework.Readback()));
        Add(row, RowStatus, (Func<object?>)(() => new Dictionary<string, object?>
        {
            ["state"] = _model.StateName,
            ["status"] = ModFrameworkLine.NameOf(Framework.Status),
            ["word"] = Framework.StatusWord,
            ["tone"] = LabelTones.Name(Framework.StatusTone),
            ["cell"] = ModFrameworkLine.CellStatusWord,
        }));
        if (line.ActionCaption is { } actionLabel)
            Add(row, RowAction, new Dictionary<string, object?>
            {
                ["label"] = actionLabel,
                ["tone"] = LabelTones.Name(line.ActionTone),
                ["cell"] = ModFrameworkLine.CellAction,
                ["enabled"] = line.ActionEnabled,
                ["reason"] = line.ActionReason,
            });
        if (_model.Uninstall is { } plan) Add(row, RowUninstall, plan.Readback());

        // The title and header readbacks ride on the title row (default) or the bar (custom header).
        var titleHost = (IUiHandle?)TitleRow ?? row;
        Add(titleHost, RowTitle, _model.Name);
        Add(titleHost, RowHeader, new Dictionary<string, object?>
        {
            ["kind"] = HeaderKind,
            ["error"] = HeaderError ?? "",
            ["rows"] = HeaderRows,
        });
        Add(titleHost, RowMeta, new Dictionary<string, object?>
        {
            ["version"] = _model.MetaVersion,
            ["released"] = _model.MetaReleased,
            ["author"] = _model.MetaAuthor,
            ["line"] = _model.MetaLine,
        });
    }

    /// <summary>The Help button's tooltip is showing (tests and the readback).</summary>
    internal bool HelpTooltipShown { get; private set; }

    /// <summary>
    /// The pointer entered or left the Help button: the kit tooltip beside it says what it opens, and the description
    /// column says the same, so the words are there with a controller too.
    /// </summary>
    private void HelpHover(IUiCellRow row, bool entered, string tip)
    {
        if (entered) Details?.Show(ModPageCopy.HelpCaption, tip + ".");
        else Details?.ShowRest();
        var runtime = WidgetRuntime.Current;
        if (runtime == null) { HelpTooltipShown = entered; return; }
        try
        {
            if (!entered)
            {
                if (HelpTooltipShown) runtime.HideTooltip();
                HelpTooltipShown = false;
                return;
            }

            var rect = row is UiCellRowHandle handle ? handle.NavRect(CellHelp) : ScreenRect.Unknown;
            if (!rect.IsKnown) return;
            runtime.ShowTooltip(rect, ModPageCopy.HelpCaption, tip,
                () => !row.IsDisposed && row.Visible && (row is UiCellRowHandle h && h.NavRect(CellHelp).IsKnown));
            HelpTooltipShown = true;
        }
        catch { /* a tooltip is optional */ }
    }

    /// <summary>The attention banner under the bar, at the margin, wrapped inside the content.</summary>
    private void BuildBanner(IUiPanel panel)
    {
        if (_model.Warning is not { } warning || _model.WarningBody is not { } body) return;
        float m = ModPageSpacing.Margin;
        float textWidth = Math.Max(CellLayout.MinTextWidth,
            RowWidth - 2f * m - HeadBlockLayout.MarkerWidth - HeadBlockLayout.RightColumnInset - CellLayout.Gap);
        var spec = new CellRowSpec(HeadKey + "-warning", ModPageSpacing.WithLead(new[]
        {
            new CellSpec(CellKind.Text, HeadBlockLayout.CellWarningMarker).With(
                text: ModPageCopy.WarningMarker, width: CellWidth.Px(HeadBlockLayout.MarkerWidth),
                minWidth: HeadBlockLayout.MarkerWidth, tone: CellTone.Danger,
                fontSize: HeadBlockLayout.WarningFontSize, vAlign: CellVAlign.Top),
            new CellSpec(CellKind.Text, HeadBlockLayout.CellWarning).With(
                text: body, width: CellWidth.Px(textWidth),
                wrap: CellWrap.Wrap, vAlign: CellVAlign.Top, fontSize: HeadBlockLayout.WarningFontSize,
                tone: CellTone.Orange, minWidth: CellLayout.MinTextWidth),
        }), height: HeadBlockLayout.WrappedRowHeight(MenuPanelLayout.WrappedLines(body, textWidth, HeadBlockLayout.WarningFontSize, HeadBlockLayout.MaxWrapLines)),
            rightInset: HeadBlockLayout.RightColumnInset + m, plain: true);
        Add(panel.CellRow(spec), RowWarning, warning);
    }

    /// <summary>Details: the description column on, then one grid of facts.</summary>
    private void BuildDetails(IUiPanel panel, ModPageActions actions)
    {
        var line = Framework;
        Details = ModPageDetails.Begin(panel, _model.Name, _model.PluginId);
        panel.SettingsDescriptionMode(true);
        panel.SettingsGrid(true);
        try
        {
            DetailsHeadingRow = panel.CellRow(SectionHeading("details", DetailsHeading));
            Add(DetailsHeadingRow, RowDetails, DetailsReadback());
        }
        finally { panel.SettingsGrid(false); }

        if (line.SidesWord.Length > 0)
        {
            var chip = new CellSpec(CellKind.Badge, ModFrameworkLine.CellSides).With(text: line.SidesWord, tone: ModFrameworkLine.SidesTone(line.Sides));
            chip.Width = CellWidth.Px(CellLayout.AutoWidth(chip));
            var sides = AddFact(panel, "sides", FactSides, new[] { chip }, SidesHelp, textValue: false);
            Add(sides, RowBadges, new[] { line.SidesWord });
        }

        if (line.Facts.PerSide is { } perSide)
        {
            AddFact(panel, "client", FactClient, new[] { FactText(CellFactValue, perSide.Client.Text.Replace("\n", " "), InstallModsPane.SideTone(perSide.Client.Tone)) }, ClientHelp);
            ServerFactRow = AddFact(panel, "server", FactServer, new[] { FactText(CellFactValue, perSide.Server.Text.Replace("\n", " "), InstallModsPane.SideTone(perSide.Server.Tone)) }, ServerHelp);
        }
        else if (line.ServerPartText.Length > 0 || line.Facts.Sides == ModSides.Both)
        {
            // A two-sided mod always has the row (hidden while nothing is known), so it can be rewritten in place when the
            // server's handshake evidence arrives or ends.
            var server = AddFact(panel, "server", FactServer, new[] { FactText(CellFactValue, line.ServerPartText, CellTone.Muted) }, ServerHelp);
            ServerFactRow = server;
            server.Visible = line.ServerPartText.Length > 0;
        }

        // C5: a declared range that admits other versions, in words, beside where the mod runs.
        if (actions.AcrossVersionsRange.Length > 0)
        {
            string words = ModAcrossVersions.RangeWords(actions.AcrossVersionsRange);
            var across = AddFact(panel, "across", ModAcrossVersions.Words, new[] { FactText(CellFactValue, words, maxWidth: ModPageSpacing.TextValueWidth(FactsWidth)) }, ModAcrossVersions.Help);
            Add(across, RowCompat, new Dictionary<string, object?>
            {
                ["acrossVersions"] = true,
                ["range"] = actions.AcrossVersionsRange,
                ["words"] = words,
            });
        }

        if (_model.MetaReleased.Length > 0)
            AddFact(panel, "released", FactReleased, new[] { FactText(CellFactValue, _model.MetaReleased) }, ReleasedHelp);

        var storeButtons = StoreButtons(_model.StoreUrl, actions.Share != null);
        IUiCellRow? storeRow = null;
        if (storeButtons.Count > 0)
        {
            storeRow = AddFact(panel, "store", FactStore, storeButtons, StoreHelp, textValue: false);
            BindButtonRow(storeRow, actions);
        }

        // The mod's own pages, together in one row.
        var pageButtons = PageButtons(actions.Pages);
        IUiCellRow? pagesRow = null;
        if (pageButtons.Count > 0)
        {
            pagesRow = AddFact(panel, "pages", FactPages, pageButtons, PagesHelp, textValue: false);
            BindButtonRow(pagesRow, actions);
        }

        ButtonRow = storeRow ?? pagesRow;
        var readbackHost = (IUiHandle?)ButtonRow ?? FrameworkRow;
        AddStoreReadback(readbackHost);
        AddShareReadback(readbackHost, actions.Share != null);

        // The Mods tab's own rows: Managed by, Update, Kept versions, Updates and the failed versions.
        actions.Notes?.Invoke(panel);

        if (!_model.HasProgenitorSettings)
            AddFact(panel, "settings", FactSettings, new[] { FactText(CellFactValue, NoSettingsFact, CellTone.Muted) }, ModPageCopy.NoProgenitorSettings);

        Details.ShowRest();
    }

    internal const string RowDetails = "page-details";

    /// <summary>The Details heading row, once built.</summary>
    internal IUiCellRow? DetailsHeadingRow { get; private set; }

    private Func<object?> DetailsReadback() => () => new Dictionary<string, object?>
    {
        ["column"] = new Dictionary<string, object?>
        {
            ["heading"] = Details?.Shown.Heading ?? "",
            ["body"] = Details?.Shown.Body ?? "",
        },
        ["factsWidth"] = FactsWidth,
        ["x1"] = ModPageSpacing.X1(FactsWidth),
        ["margin"] = ModPageSpacing.Margin,
    };

    /// <summary>
    /// About: the description at the margin, wrapped at a readable width. ABOUTSRC1: the text is
    /// <see cref="ModPageModel.About"/> (a verified listing's README, else the plugin's own), drawn by the
    /// same Markdown renderer whatever its source; <c>page-readme.source</c> says which it was.
    /// </summary>
    private void BuildAbout(IUiPanel panel)
    {
        string markdown = _model.About.Markdown;
        string source = _model.About.SourceName;
        List<MarkdownBlock> blocks = markdown.Length == 0
            ? new List<MarkdownBlock>()
            : MarkdownPlain.CapBlocks(MarkdownPlain.RenderBlocks(markdown, int.MaxValue), MarkdownPlain.DefaultMaxChars);
        ReadmeBlocks = blocks.Count;
        panel.CellRow(SectionHeading("about", AboutHeading));
        float m = ModPageSpacing.Margin;
        float width = Math.Max(CellLayout.MinTextWidth, Math.Min(ModPageSpacing.ReadableWidth, RowWidth - 2f * m - HeadBlockLayout.RightColumnInset));
        if (blocks.Count == 0)
        {
            var none = panel.CellRow(new CellRowSpec("page-about-0", ModPageSpacing.WithLead(new[]
            {
                new CellSpec(CellKind.Muted, "text").With(text: ModPageCopy.NoDescription, width: CellWidth.Px(width), vAlign: CellVAlign.Middle),
            }), height: 30f, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
            Add(none, RowReadme, new Dictionary<string, object?> { ["state"] = "unavailable", ["chars"] = 0, ["blocks"] = 0, ["source"] = "none" });
            return;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            string text = blocks[i].Text;
            int lines = MenuPanelLayout.WrappedLines(text, width, CellLayout.TextFontSize, 40);
            var row = panel.CellRow(new CellRowSpec("page-about-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ModPageSpacing.WithLead(new[]
            {
                new CellSpec(CellKind.Text, "text").With(text: text, width: CellWidth.Px(width), wrap: CellWrap.Wrap, vAlign: CellVAlign.Top),
            }), height: AboutRowHeight(lines), rightInset: HeadBlockLayout.RightColumnInset, plain: true));
            if (i == 0)
                Add(row, RowReadme, new Dictionary<string, object?>
                {
                    ["state"] = "ready",
                    ["chars"] = MarkdownPlain.ContentLength(blocks),
                    ["blocks"] = blocks.Count,
                    ["source"] = source,
                });
        }
    }

    /// <summary>C5: the Works across versions row's readback: <c>page-compat {acrossVersions, range, words}</c>.</summary>
    internal const string RowCompat = "page-compat";

    /// <summary>C5: the For developers heading's readback: <c>page-dev {heading, expanded, rows[{key, name, value}]}</c>.</summary>
    internal const string RowDev = "page-dev";

    /// <summary>C5: each developer row's readback: <c>page-dev-row {key, name, value}</c> (besides its <c>page-fact</c>).</summary>
    internal const string RowDevRow = "page-dev-row";

    /// <summary>The For developers heading row, once built (null when the mod has no section).</summary>
    internal IUiCellRow? DevHeadingRow { get; private set; }

    /// <summary>
    /// C5: <b>For developers</b>, the page's last section, after About: a secondary section for mod authors, so it never
    /// pushes the player's sections (Details, Settings, About) down. Collapsed by default; the heading's fold marker turns
    /// when expanded and the whole heading takes the click. Expanded, its rows are Details grid rows (<see cref="FactRow"/>)
    /// described in the same column. Only a Progenitor plugin loaded on this side has it (<see cref="ModPageActions.Developer"/>).
    /// </summary>
    private void BuildDeveloper(IUiPanel panel, ModPageActions actions)
    {
        if (actions.Developer is not { } facts) return;
        bool expanded = actions.DeveloperExpanded;
        var rows = ModDevDetails.Rows(facts);
        panel.SettingsGrid(true);
        IUiCellRow heading;
        try { heading = panel.CellRow(ModDevDetails.HeadingRow()); }
        finally { panel.SettingsGrid(false); }
        DevHeadingRow = heading;
        heading.SetExpanded(expanded);
        // The marker is not a button cell: the row click covers it, as on the list's group headings.
        if (actions.ToggleDeveloper != null) heading.OnRowClick(actions.ToggleDeveloper);

        var readRows = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var r in rows)
            readRows.Add(new Dictionary<string, object?> { ["key"] = r.Key, ["name"] = r.Name, ["value"] = r.Value });
        Add(heading, RowDev, new Dictionary<string, object?>
        {
            ["heading"] = ModDevDetails.Heading,
            ["expanded"] = expanded,
            ["rows"] = readRows,
        });
        if (!expanded) return;
        foreach (var r in rows)
        {
            var row = AddFact(panel, r.Key, r.Name, new[] { FactText(CellFactValue, r.Value, maxWidth: ModPageSpacing.TextValueWidth(FactsWidth)) }, r.Help);
            Add(row, RowDevRow, new Dictionary<string, object?> { ["key"] = r.Key, ["name"] = r.Name, ["value"] = r.Value });
        }
    }

    /// <summary>An About paragraph's height: its wrapped lines at the body size, plus a small pad.</summary>
    internal static float AboutRowHeight(int lines) => Math.Max(1, lines) * (CellLayout.TextFontSize * 1.45f) + 8f;
}
