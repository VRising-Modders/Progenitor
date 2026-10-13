using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>The handlers the mod page's controls need. Every one of them is optional.</summary>
internal sealed class ModPageActions
{
    /// <summary>The action button (Uninstall / Reactivate / Cancel install) was pressed: open the prompt.</summary>
    internal Action? Action { get; set; }

    /// <summary>The prompt's yes.</summary>
    internal Action? Yes { get; set; }

    /// <summary>The prompt's no.</summary>
    internal Action? No { get; set; }

    /// <summary>The store-link control: open the listing's package page.</summary>
    internal Action? Store { get; set; }

    /// <summary>
    /// Chat stage 3 (mod links): <b>Share in chat</b> in the header's button row. Bound only when the
    /// mod's Thunderstore package is known; null draws no button.
    /// </summary>
    internal Action? Share { get; set; }

    /// <summary>SETTINGS1 stage 3: the on/off switch was pressed (bound only while it can act).</summary>
    internal Action? Switch { get; set; }

    /// <summary>SETTINGS1 stage 3: Retry on a hot-loadable mod that failed to load.</summary>
    internal Action? Retry { get; set; }

    /// <summary>SETTINGS1 stage 3: Show all — the notice with every problem and the report.</summary>
    internal Action? ShowAllProblems { get; set; }

    /// <summary>KEYS2: <b>Dismiss</b> on a dismissible problem's line.</summary>
    internal Action<ModProblem>? DismissProblem { get; set; }

    /// <summary>Draws the Progenitor settings rows inside the Settings section (the menu owns them).</summary>
    internal Action<IUiPanel>? SettingsRows { get; set; }

    /// <summary>
    /// SETTINGS1: the mod's settings drawn inline in sections. When set it replaces
    /// <see cref="SettingsRows"/>; its readback keys are registered against this page.
    /// </summary>
    internal ModSettingsView? Settings { get; set; }

    /// <summary>
    /// SETTINGS1: the mod's own header content (<see cref="ModMenuRegistration.Header"/>), drawn in
    /// the mod area under the framework line in place of the default title and by-line. Null draws
    /// the default.
    /// </summary>
    internal Action<IUiPanel>? Header { get; set; }

    /// <summary>Drawn between the head and the Settings section: provenance, notes, the update offer.</summary>
    internal Action<IUiPanel>? Notes { get; set; }

    /// <summary>
    /// SETTINGS1 stage 2: the mod's custom pages (<see cref="ModMenuRegistration.Page"/>), drawn as
    /// small buttons in the header's button row beside Like, in registration order.
    /// </summary>
    internal IReadOnlyList<(string Title, Action Open)> Pages { get; set; } = Array.Empty<(string, Action)>();

    /// <summary>
    /// C5: the declared range when it admits other versions (<see cref="ModAcrossVersions.Applies"/>); the Details grid then
    /// has the <b>Works across versions</b> row. Empty draws no row.
    /// </summary>
    internal string AcrossVersionsRange { get; set; } = "";

    /// <summary>C5: the For developers section's facts; null (a mod that is not a Progenitor plugin loaded here) draws no section.</summary>
    internal ModDevFacts? Developer { get; set; }

    /// <summary>C5: the For developers section is expanded.</summary>
    internal bool DeveloperExpanded { get; set; }

    /// <summary>C5: the For developers heading was clicked.</summary>
    internal Action? ToggleDeveloper { get; set; }
}

/// <summary>
/// MODSPAGE1: the Mods-tab mod page, built from <see cref="ModPageModel"/> and answering the
/// <c>mods_ui widget_read</c> extras for its own rows.
///
/// <para><b>KIT1.</b> The five kit gaps MODSPAGE1 reported are closed, so the page is the mock rather
/// than the words of the mock: the head is one <see cref="UiHeadBlock"/> — a two-column block whose
/// right column carries the colored status word, the action button and the side badges beside the
/// title, meta line and one-liner instead of under them — the status and the badges are colored from
/// <see cref="MenuPalette"/>, the store link is an <see cref="IUiIconLink"/> beside the Settings
/// heading, the title is drawn at the mock's display size, and each y/n prompt is one
/// <see cref="UiPromptBlock"/>. The sidebar's marker and dot moved into the row itself
/// (<see cref="IUiMarkedButton"/>, built by <see cref="ModsMenu"/>).</para>
///
/// <para>The readback keys are unchanged. What is new is that several of them now sit on the same
/// row — a cell row describes itself with a rect per cell, and this pane's extras merge onto it — so
/// a reader gets <c>page-title</c> and <c>page-status</c> on the title row, <c>page-meta</c>,
/// <c>like</c> and <c>page-action</c> on the meta row, and <c>page-oneliner</c> with
/// <c>page-badges</c> on the third. Every color is still named in the readback, so nothing has to be
/// inferred from a pixel.</para>
///
/// <para><b>What is still a gap.</b> Two-line sidebar rows (MODS1). And <c>mods_ui rects</c> finds a
/// row by matching a TMP caption against the readback label, which a cell row's key is not, so the
/// head block's geometry is read from the per-cell <c>rect</c> in <c>widget_read</c> rather than from
/// <c>rects</c>; teaching <c>rects</c> about cell rows belongs to the op's owner.</para>
/// </summary>
internal sealed partial class ModPagePane
{
    // Readback row keys (docs/mods-menu.md, docs/api.md).
    internal const string RowTitle = "page-title";
    internal const string RowMeta = "page-meta";
    internal const string RowOneLiner = "page-oneliner";
    internal const string RowStatus = "page-status";
    internal const string RowAction = "page-action";
    internal const string RowBadges = "page-badges";
    internal const string RowWarning = "page-warning";
    internal const string RowStoreLink = "page-store-link";
    internal const string RowSettings = "page-settings";
    internal const string RowReadme = "page-readme";
    internal const string RowPrompt = "modPrompt";

    /// <summary>SRVUNINST1: the Uninstall plan's readback (per-side removers, reasons, the confirmation body).</summary>
    internal const string RowUninstall = "page-uninstall";

    /// <summary>SETTINGS1: the framework line — switch, status, version, sides, server part.</summary>
    internal const string RowFramework = "page-framework";

    /// <summary>SETTINGS1: which header the mod area drew — <c>default</c> or <c>custom</c>.</summary>
    internal const string RowHeader = "page-header";

    /// <summary>The framework line's cell-row key.</summary>
    internal const string FrameworkKey = "page-framework";

    private readonly List<(IUiHandle Handle, string Key, object? Value)> _rows = new();
    private readonly ModPageModel _model;
    private readonly ModPromptKind _prompt;

    internal ModPagePane(ModPageModel model, ModPromptKind prompt, ModFrameworkLine? framework = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _prompt = prompt;
        Framework = framework ?? ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            PluginId = model.PluginId,
            PageState = model.State,
            Version = model.Version,
            Sides = ModFrameworkLine.DecideSides(model.Listing.Side, model.RequiresBoth),
            ActionCaption = model.ActionLabel,
            ActionTone = ActionTone(model.State),
        });
    }

    /// <summary>
    /// SETTINGS1: the framework line this page draws first. Stage 2: recomposed in place when a
    /// needs-restart setting changes (<see cref="SetSettingsPendingRestart"/>).
    /// </summary>
    internal ModFrameworkLine Framework { get; private set; }

    /// <summary>
    /// SETTINGS1 stage 2: a needs-restart setting on this page now differs (or no longer differs) from
    /// its value at load. The status cell is rewritten in place; the readback follows at once.
    /// </summary>
    internal void SetSettingsPendingRestart(bool pending)
    {
        if (Framework.Facts.SettingsPendingRestart == pending) return;
        Framework = ModFrameworkLine.Compose(Framework.Facts.WithSettingsPendingRestart(pending));
        try
        {
            FrameworkRow?.SetCell(
                ModFrameworkLine.CellStatusWord, Framework.StatusWord, LabelTones.ToCellTone(Framework.StatusTone));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: framework status rewrite threw: " + e.Message); }
    }

    /// <summary>
    /// COUNTERPART-SAMPLES: the joined server's handshake evidence about this mod's server part
    /// arrived or ended. The status bar's server-part cell is rewritten in place (shown or hidden) and
    /// the settings rows are re-grayed; nothing is rebuilt. Returns whether anything changed.
    /// </summary>
    internal bool SetServerPart(ServerPartState part, bool limited)
    {
        var facts = Framework.Facts;
        bool lineChanged = facts.ServerPart != part || facts.Limited != limited;
        if (lineChanged)
        {
            Framework = ModFrameworkLine.Compose(facts.WithServerPart(part, limited));
            try
            {
                // MODPAGE2: the server part is the Details grid's Server row (a per-side page keeps its own words there).
                string text = Framework.ServerPartText;
                if (ServerFactRow != null && Framework.Facts.PerSide == null)
                {
                    ServerFactRow.SetCell(CellFactValue, text);
                    ServerFactRow.Visible = text.Length > 0;
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: framework server-part rewrite threw: " + e.Message); }
        }

        try { SettingsView?.Reevaluate(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings re-gray threw: " + e.Message); }
        return lineChanged;
    }

    /// <summary>The page-button cell key of custom page <paramref name="index"/> in the header's button row.</summary>
    internal static string PageButtonKey(int index) => "page-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The framework line's cell row, once built.</summary>
    internal IUiCellRow? FrameworkRow { get; private set; }

    /// <summary><c>default</c> or <c>custom</c>: which header the mod area drew.</summary>
    internal string HeaderKind { get; private set; } = "default";

    /// <summary>How many rows the mod's own header drew (0 for the default).</summary>
    internal int HeaderRows { get; private set; }

    /// <summary>The message of a mod header builder that threw, or null.</summary>
    internal string? HeaderError { get; private set; }

    /// <summary>The inline settings this build drew, or null.</summary>
    internal ModSettingsView? SettingsView { get; private set; }

    internal ModPageModel Model => _model;

    internal ModPromptKind OpenPrompt => _prompt;

    /// <summary>How many README blocks the page drew (0 when the listing is unknown).</summary>
    internal int ReadmeBlocks { get; private set; }

    /// <summary>The open prompt's block, or null when no prompt is open.</summary>
    internal UiPromptBlock? PromptBlock { get; private set; }

    /// <summary>The pane row width the head block was laid out for.</summary>
    internal float RowWidth { get; private set; }

    /// <summary>
    /// True once <see cref="Build"/> has asked the pane to drop its own centered title (KIT1 gen 1).
    /// </summary>
    internal bool PaneTitleHidden { get; private set; }

    /// <summary>The mock's body alignment: left, the same mode the Configuration tab's rows use.</summary>
    internal const string BodyAlignment = SettingsAlign.Left;

    /// <summary>The status word's tone: green installed, yellow pending-load, orange pending-uninstall.</summary>
    internal static LabelTone StatusTone(ModPageState state) => state switch
    {
        ModPageState.PendingLoad => LabelTone.Pending,
        ModPageState.PendingUninstall => LabelTone.Orange,
        _ => LabelTone.Ok,
    };

    /// <summary>
    /// The action button's tone. Uninstall is the destructive one, so it wears the mock's orange;
    /// the two that put a state back wear the state they restore.
    /// </summary>
    internal static LabelTone ActionTone(ModPageState state) => state switch
    {
        ModPageState.PendingUninstall => LabelTone.Ok,
        ModPageState.PendingLoad => LabelTone.Warn,
        _ => LabelTone.Orange,
    };

    /// <summary>
    /// MODPAGE2 (layout approved live 2026-10-09): build the whole page into <paramref name="panel"/>, top to bottom: the
    /// title and by-line (or the mod's own header), the actions bar, the attention banner and problem lines, Details (a grid
    /// of facts with the description column), Settings when the mod has some, and About. Spacing: <see cref="ModPageSpacing"/>.
    /// </summary>
    internal void Build(IUiPanel panel, ModPageActions actions)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        actions ??= new ModPageActions();

        // KIT1 gen 1, live at 3840x1080: the pane drew its own centered title above the head block,
        // so the mod's name appeared twice. The overview and custom pages keep their title.
        Ui.HidePaneTitle(panel);
        PaneTitleHidden = true;
        panel.ContentAlignment = BodyAlignment;
        RowWidth = MenuPanelLayout.DetailRowWidth();

        BuildTitle(panel, actions);
        BuildActionsBar(panel, actions);
        BuildBanner(panel);
        BuildProblems(panel, actions);
        BuildPrompt(panel, actions);
        BuildDetails(panel, actions);
        BuildSettings(panel, actions);
        BuildAbout(panel);
        BuildDeveloper(panel, actions);
    }

    // ---- SETTINGS1 stage 3: the problems line --------------------------------------------------

    internal const string RowProblems = "page-problems";
    internal const string RowProblem = "page-problem";
    internal const string ProblemsKey = HeadKey + "-problems";
    internal const string CellProblemMarker = "marker";
    internal const string CellProblemText = "text";
    internal const string CellRetry = "retry";
    internal const string CellShowAll = "show-all";
    internal const string CellDismiss = "dismiss";
    internal const string DismissCaption = "Dismiss";
    internal const string RetryCaption = "Retry";
    internal const float ProblemFontSize = ModSettingsGrid.HelpFontSize;

    /// <summary>The problem-line rows this build drew, in order (tests and the readback).</summary>
    internal List<IUiCellRow> ProblemRows { get; } = new();

    /// <summary>The Retry / Show all row, or null when there is no problem.</summary>
    internal IUiCellRow? ProblemActionsRow { get; private set; }

    /// <summary>
    /// Directly under the status bar: one plain sentence per problem (what happened and what to do),
    /// worst first, at most <see cref="ModProblems.PageLineCap"/>; then <b>Retry</b> (a hot-loadable
    /// mod that failed to load) and <b>Show all</b>, which opens the notice with every problem and
    /// the report for the mod author. Nothing here disables anything.
    /// </summary>
    private void BuildProblems(IUiPanel panel, ModPageActions actions)
    {
        var problems = Framework.Facts.Problems;
        if (problems == null || problems.Count == 0) return;
        var lines = ModProblems.PageLines(problems, out bool showAll);
        bool retry = actions.Retry != null && HasLoadFailure(problems) && Framework.Facts.CanRetry;
        var specs = ProblemRowSpecs(problems, lines, showAll, retry, RowWidth);
        for (int i = 0; i < specs.Count; i++)
        {
            var row = panel.CellRow(specs[i]);
            ProblemRows.Add(row);
            var readback = problems[i].Readback();
            readback["line"] = lines[i];
            Add(row, RowProblem, readback);
            var problem = problems[i];
            if (problem.Dismissible && actions.DismissProblem != null)
                row.OnClick(CellDismiss, () => actions.DismissProblem(problem));
            if (i != specs.Count - 1) continue;
            // The last line carries Retry and Show all at its right end, and the summary readback.
            ProblemActionsRow = row;
            if (retry && actions.Retry != null) row.OnClick(CellRetry, actions.Retry);
            if (showAll && actions.ShowAllProblems != null) row.OnClick(CellShowAll, actions.ShowAllProblems);
            Add(row, RowProblems, new Dictionary<string, object?>
            {
                ["count"] = problems.Count,
                ["shown"] = lines.Count,
                ["lines"] = lines,
                ["showAll"] = showAll,
                ["retry"] = retry,
                ["conflicts"] = ModProblems.ConflictCount(problems),
            });
        }
    }

    /// <summary>
    /// Live 2026-09-29 (3840x1080, the Sample's one conflict): the problem line was drawn centered in
    /// orange at body size, and <b>Show all</b> was a row of its own between it and the mod's title.
    /// Now each problem is one left-aligned line directly under the status bar, at the help-text size
    /// (<see cref="ModSettingsGrid.HelpFontSize"/>), in the warn tone for a warning and the danger
    /// tone for a critical problem; <b>Retry</b> and <b>Show all</b> are small buttons at the right
    /// end of the last line; no other row is added, so the gap to the mod's title is the ordinary row
    /// gap. Game-free.
    /// </summary>
    internal static List<CellRowSpec> ProblemRowSpecs(
        IReadOnlyList<ModProblem> problems, IReadOnlyList<string> lines, bool showAll, bool retry, float rowWidth)
    {
        var specs = new List<CellRowSpec>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            bool last = i == lines.Count - 1;
            var buttons = new List<CellSpec>(3);
            if (problems[i].Dismissible) buttons.Add(SmallButton(CellDismiss, DismissCaption, CellTone.Normal));
            if (last && retry) buttons.Add(SmallButton(CellRetry, RetryCaption, CellTone.Gold));
            if (last && showAll) buttons.Add(SmallButton(CellShowAll, ModProblems.ShowAllCaption, CellTone.Normal));
            float reserved = 0f;
            foreach (var bt in buttons) reserved += bt.Width.Value + CellLayout.Gap;
            float textWidth = Math.Max(CellLayout.MinTextWidth,
                Math.Max(1f, rowWidth - HeadBlockLayout.RightColumnInset) - reserved - CellLayout.Gap);
            var cells = new List<CellSpec>(4)
            {
                new CellSpec(CellKind.Text, CellProblemText).With(
                    text: lines[i], width: CellWidth.Px(textWidth), align: CellAlign.Left,
                    wrap: CellWrap.Wrap, vAlign: CellVAlign.Top, fontSize: ProblemFontSize,
                    tone: ProblemTone(problems[i].Severity), minWidth: CellLayout.MinTextWidth),
                new CellSpec(CellKind.Spacer, HeadBlockLayout.CellGap),
            };
            cells.AddRange(buttons);
            int wrapped = MenuPanelLayout.WrappedLines(lines[i], textWidth, ProblemFontSize, HeadBlockLayout.MaxWrapLines);
            specs.Add(new CellRowSpec(
                ProblemsKey + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), cells,
                height: ProblemRowHeight(wrapped), rightInset: HeadBlockLayout.RightColumnInset, plain: true));
        }

        return specs;
    }

    /// <summary>A problem line's tone: danger for critical, warn for a warning, muted for info.</summary>
    internal static CellTone ProblemTone(ModProblemSeverity severity) => severity switch
    {
        ModProblemSeverity.Critical => CellTone.Danger,
        ModProblemSeverity.Warning => CellTone.Warn,
        _ => CellTone.Muted,
    };

    /// <summary>Help-size lines: this tall each, plus a small pad (guessed; not live-checked).</summary>
    internal static float ProblemRowHeight(int lines) => Math.Max(1, lines) * ProblemLineHeight + 6f;

    internal const float ProblemLineHeight = 20f;
    internal const float ProblemButtonFontSize = 14f;

    private static CellSpec SmallButton(string key, string caption, CellTone tone)
    {
        float w = CellLayout.EstimateTextWidth(caption, ProblemButtonFontSize) * 1.3f + CellLayout.ButtonPad;
        var b = new CellSpec(CellKind.Button, key).With(text: caption, tone: tone, align: CellAlign.Right,
            fontSize: ProblemButtonFontSize, width: CellWidth.Px(w), minWidth: w);
        return b;
    }

    private static bool HasLoadFailure(IReadOnlyList<ModProblem> problems)
    {
        foreach (var p in problems)
            if (p.Kind == ModProblemKinds.LoadFailed) return true;
        return false;
    }


    /// <summary>SETTINGS1 stage 2: the header's button row (custom pages and Like), once built.</summary>
    internal IUiCellRow? ButtonRow { get; private set; }

    private static void BindButtonRow(IUiCellRow row, ModPageActions actions)
    {
        if (actions.Store != null) row.OnClick(CellStore, actions.Store);
        if (actions.Share != null) row.OnClick(CellShare, actions.Share);
        var pages = actions.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            var open = pages[i].Open;
            if (open != null) row.OnClick(PageButtonKey(i), open);
        }
    }

    /// <summary>The store button's cell key in the header's button row.</summary>
    internal const string CellStore = "store";

    /// <summary>
    /// SETTINGS1 stage 2: the store link is a <b>Store page</b> button in the header's button row, drawn
    /// only when a store url is known. The readback is always on that row, so it carries the url —
    /// empty when no listing names this plugin.
    /// </summary>
    private void AddStoreReadback(IUiHandle? row)
    {
        Add(row, RowStoreLink, new Dictionary<string, object?>
        {
            ["kind"] = "button",
            ["caption"] = ModPageCopy.StoreLinkCaption,
            ["tooltip"] = ModPageCopy.StoreLinkTooltip,
            ["url"] = _model.StoreUrl ?? "",
            ["cell"] = CellStore,
            ["shown"] = !string.IsNullOrWhiteSpace(_model.StoreUrl),
        });
    }

    /// <summary>Chat stage 3 (mod links): the <b>Share in chat</b> button's cell key in the header's button row.</summary>
    internal const string CellShare = "share";

    /// <summary>Readback key of the share button: <c>page-share {kind, caption, cell, shown}</c>.</summary>
    internal const string RowShare = "page-share";

    private void AddShareReadback(IUiHandle? row, bool shown)
    {
        Add(row, RowShare, new Dictionary<string, object?>
        {
            ["kind"] = "button",
            ["caption"] = ModLinks.ShareCaption,
            ["cell"] = CellShare,
            ["shown"] = shown,
        });
    }

    /// <summary>The head block's row-key prefix; its rows are <c>page-title</c>, <c>page-meta</c>, …</summary>
    internal const string HeadKey = "page";

    /// <summary>
    /// INSTALLUX2 item 1: where the y/n prompt's panel comes from. The pane never names the runtime,
    /// so a game-free test hands it a fake panel; production is <see cref="Ui.Panel"/> through
    /// <see cref="ModsMenu"/>. Null means "no dialog can be opened" — the prompt is then not drawn at
    /// all rather than drawn back into the page, which is the in-pane block the user rejected.
    /// </summary>
    internal static Func<string, IUiPanel>? DialogFactory { get; set; }

    /// <summary>The dialog this build opened, or null when no prompt is open.</summary>
    internal UiDialog? PromptDialog { get; private set; }

    /// <summary>
    /// INSTALLUX2 item 1: the prompt is a modal dialog in the middle of the screen, not an in-pane
    /// block. The user, on the INSTALLUX1 build: "it expands like the old behavior asking me if I
    /// want to stall like this instead of popping up a dialogue."
    ///
    /// <para>The copy, the <c>modPrompt {kind, open}</c> readback and the widget keys are the ones
    /// the in-pane block published — the tag is still <c>modPrompt-&lt;kind&gt;</c>, so the buttons
    /// row is still <c>modPrompt-&lt;kind&gt;-buttons</c> with <c>yes</c> / <c>no</c> cells and a
    /// bridge client's <c>widget_click</c> keeps working. What is new in the readback is
    /// <c>presentation: "dialog"</c>.</para>
    ///
    /// <para><paramref name="panel"/> is the page's own panel and is deliberately unused: nothing
    /// about the confirmation is drawn in it any more.</para>
    /// </summary>
    private void BuildPrompt(IUiPanel panel, ModPageActions actions)
    {
        _ = panel;
        if (_prompt == ModPromptKind.None)
        {
            // A page rebuilt with no prompt open must not leave the previous one on screen. Only a
            // dialog this page opened is closed; the Install tab's dialog carries its own tag.
            CloseOwnDialog();
            return;
        }

        var factory = DialogFactory;
        if (factory == null) return;
        string kind = ModPageStates.Name(_prompt);
        var dialog = UiDialog.Open(
            factory,
            RowPrompt + "-" + kind,
            kind,
            _model.PluginId,
            _model.PromptHeading(_prompt),
            _model.PromptBody(_prompt),
            ModPageModel.PromptYes(_prompt),
            PromptYesTone(_prompt),
            ModPageModel.PromptNo(_prompt),
            facts: _model.PromptFacts(_prompt),
            danger: PromptIsDestructive(_prompt));
        PromptDialog = dialog;
        var block = dialog.Prompt;
        PromptBlock = block;
        // The dialog closes itself before it calls these, and Escape answers it with No, so the
        // page's No handler runs on Escape too.
        if (actions.Yes != null) dialog.OnYes(actions.Yes);
        if (actions.No != null) dialog.OnNo(actions.No);
        Add(block.Heading, RowPrompt, Prompt(kind, "heading"));
        Add(block.Body, RowPrompt, Prompt(kind, "body"));
        var buttons = Prompt(kind, "buttons");
        buttons["yes"] = ModPageModel.PromptYes(_prompt);
        buttons["no"] = ModPageModel.PromptNo(_prompt);
        Add(block.Buttons, RowPrompt, buttons);
    }

    /// <summary>The <c>modPrompt</c> readback's presentation: the kit dialog, never an in-pane block.</summary>
    internal const string PromptPresentation = "dialog";

    /// <summary>
    /// Close the open dialog only when it is one of this page's three prompts. Tag-scoped so a Mods
    /// page rebuilt behind the Install tab's dialog does not dismiss it.
    /// </summary>
    internal static void CloseOwnDialog()
    {
        var open = UiDialog.Current;
        if (open == null) return;
        if (!open.Tag.StartsWith(RowPrompt, StringComparison.Ordinal)) return;
        open.Close();
    }

    /// <summary>
    /// The yes button's requested tone: the mock's orange for a removal, green for the two that keep a mod.
    /// A destructive prompt (<see cref="PromptIsDestructive"/>) is drawn in the danger tone instead.
    /// </summary>
    internal static LabelTone PromptYesTone(ModPromptKind kind) => kind switch
    {
        ModPromptKind.Reactivate => LabelTone.Ok,
        ModPromptKind.CancelInstall => LabelTone.Warn,
        _ => LabelTone.Orange,
    };

    /// <summary>
    /// The prompts whose Yes takes something away: Uninstall removes the mod and Cancel install deletes
    /// the downloaded file. Enter does not answer them (a click or a controller's right-then-A does) and
    /// their Yes is drawn in the danger tone. Reactivate only keeps a mod, so Enter still answers it.
    /// </summary>
    internal static bool PromptIsDestructive(ModPromptKind kind) =>
        kind is ModPromptKind.Uninstall or ModPromptKind.CancelInstall;

    private static Dictionary<string, object?> Prompt(string kind, string row) => new()
    {
        ["kind"] = kind,
        ["open"] = true,
        ["row"] = row,
        // INSTALLUX2 item 1: how the prompt is on screen. A reader that still expected the in-pane
        // block would otherwise have no way to tell the two builds apart from the readback alone.
        ["presentation"] = PromptPresentation,
    };

    /// <summary>Settings: drawn only when the mod has Progenitor settings (otherwise Details carries one Settings fact).</summary>
    private void BuildSettings(IUiPanel panel, ModPageActions actions)
    {
        var settingsKind = new Dictionary<string, object?> { ["kind"] = _model.SettingsKind };
        if (_model.HasProgenitorSettings && actions.Settings is { } view)
        {
            // Stage 2: the view draws the one header row (heading, search, Reset all) itself.
            SettingsView = view;
            if (Details != null) Details.View = view;
            // The section heading, like Details and About; the view's header row keeps the search and Reset all.
            panel.CellRow(SectionHeading("settings", ModPageCopy.SettingsHeading));
            view.HeaderTitle = "";
            view.Build(panel, Add);
            Add(view.HeaderRow, RowSettings, settingsKind);
            Details?.ShowRest();
            return;
        }

        if (_model.HasProgenitorSettings)
        {
            var heading = panel.CellRow(SectionHeading("settings", ModPageCopy.SettingsHeading));
            Add(heading, RowSettings, settingsKind);
            actions.SettingsRows?.Invoke(panel);
            return;
        }

        // No settings: the Settings fact in Details says so; the readback keeps its key on the Details heading.
        if (DetailsHeadingRow != null) Add(DetailsHeadingRow, RowSettings, settingsKind);
    }

    private void Add(IUiHandle? handle, string key, object? value)
    {
        if (handle == null) return;
        _rows.Add((handle, key, value));
    }

    /// <summary>
    /// The <c>mods_ui widget_read</c> extras for one of this page's rows. False for anything this
    /// page did not build.
    ///
    /// <para>KIT1: <b>every</b> key registered against the handle is returned, not the first. The
    /// mock's right column shares a row with the left one, so one cell row carries two or three of
    /// this page's keys; answering only the first is what would hide <c>page-status</c> behind
    /// <c>page-title</c>.</para>
    /// </summary>
    internal bool TryDescribeRow(IUiHandle handle, out Dictionary<string, object?>? extra)
    {
        extra = null;
        if (handle == null) return false;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!ReferenceEquals(handle, _rows[i].Handle)) continue;
            extra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            // SETTINGS1: a live value (a setting row, the framework line) is read when described.
            object? value = _rows[i].Value;
            if (value is Func<object?> live)
            {
                try { value = live(); }
                catch (Exception e) { value = "error: " + e.Message; }
            }

            extra[_rows[i].Key] = value;
        }

        return extra != null;
    }
}
