using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// KEYS1 (docs/mods-menu.md#keybinds-tab): the Keybinds tab's content pane. Draws what
/// <see cref="KeybindsTabModel"/> decides — a pinned toolbar (search, the All / Conflicts N / Changed
/// chips, the Game keys / Mod keys jumps, Jump to mod…) and the edit bar; then the game's keys under
/// the Bindings Menu's own categories (read-only) and one section per mod; Reset all mod keys pinned at
/// the bottom right — and runs the remap flow: click a mod key's cap, press the new chord
/// (<see cref="Keybinds.BeginCapture"/>: the picker's rules), see what it would clash with, then Save,
/// Unbind or Cancel. Saves go through <see cref="Keybinds.Rebind"/> (the same cfg entry the mod page's
/// Keys section writes) or, for an ordinary mod's key setting, <see cref="SettingsCatalog.WriteKey"/>.
/// Filtering hides and shows rows in place; a save rebuilds the pane (the owner restores the scroll).
///
/// <para>KEYS3: the rows look like the game's Bindings Menu — one line, the name at the left, two key
/// caps (primary, secondary) at the right drawn with the game's own key-cap sprite
/// (<see cref="KeybindArtLadder"/>), reset and clear in the right gutter — and the hovered (else the
/// selected) row is highlighted and described in the column on the right
/// (<see cref="BuildDescription"/>, <see cref="KeybindsTabLayout.Describe"/>).</para>
/// </summary>
internal sealed class KeybindsPane
{
    // Readback keys (mods_ui widget_read).
    internal const string RowToolbar = "keys-toolbar";
    internal const string RowEdit = "keys-edit";
    internal const string RowSection = "keys-section";
    internal const string RowKey = "keys-row";
    internal const string RowGameNote = "keys-game-note";
    internal const string RowFooter = "keys-footer";
    internal const string RowDescription = "keys-description";

    // Cell-row keys and cells.
    internal const string ToolbarKey = "keys-toolbar";
    internal const string EditKey = "keys-edit";
    internal const string FooterKey = "keys-footer";
    internal const string DescHeadingKey = "keys-desc-heading";
    internal const string DescOrnamentKey = "keys-desc-ornament";
    internal const string SectionKeyPrefix = "keys-section-";
    internal const string NameRowPrefix = "keys-row-";
    internal const string HelpRowPrefix = "keys-help-";
    internal const string CellHeading = "heading";
    internal const string CellOrnament = "ornament";
    internal const string CellSearch = "search";
    internal const string CellAll = "all";
    internal const string CellConflicts = "conflicts";
    internal const string CellChanged = "changed";
    internal const string CellGameJump = "game-keys";
    internal const string CellModJump = "mod-keys";
    internal const string CellResetAll = "reset-all";
    internal const string CellJumpToMod = "jump-to-mod";
    internal const string CellTitle = "title";
    internal const string CellCount = "count";
    internal const string CellOpenPage = "open-page";
    internal const string CellName = KeybindsTabLayout.CellName;
    internal const string CellKey = KeybindsTabLayout.CellPrimary;
    internal const string CellKey2 = KeybindsTabLayout.CellSecondary;
    internal const string CellReset = KeybindsTabLayout.CellReset;
    internal const string CellClear = KeybindsTabLayout.CellClear;
    internal const string CellHelp = "help";
    internal const string CellEditText = "text";
    internal const string CellSave = "save";
    internal const string CellUnbind = "unbind";
    internal const string CellCancel = "cancel";
    internal const string CellSwap = "swap";

    internal const string RowConflictsHeader = "keys-conflicts-header";

    internal const string JumpGame = "game";
    internal const string JumpMods = "mods";

    internal const string ResetAllDialogTag = "keybinds-reset-all";

    internal const string SwapDialogTag = "keybinds-swap";

    private readonly Func<IReadOnlyList<(string Id, string Name)>> _modOrder;
    private readonly Func<string, bool> _hasModPage;
    private readonly List<(IUiHandle Handle, string Key, object? Value)> _readback = new();
    private readonly List<RowBinding> _rows = new();
    private readonly List<(KeybindsSectionModel Section, IUiCellRow Heading, IUiHandle? Note)> _sections = new();
    private IUiPanel? _panel;
    private IUiCellRow? _toolbar;
    private IUiCellRow? _edit;
    private IUiCellRow? _footer;
    private IUiLabel? _noMatches;
    private IUiLabel? _conflictsHeader;

    // KEYS3: the description column (a second workspace pane) and what it tracks.
    private IUiPanel? _aside;
    private IUiCellRow? _descHeading;
    private IUiLabel? _descBody;
    private IUiToneLabel? _descWarning;
    private IUiToneLabel? _descInfo;
    private IUiToneLabel? _descHint;
    private string _hoverCell = "";
    private string _hovered = "";

    /// <summary>The row the pointer just left; it stays described until <see cref="_leaveAt"/> (the gap between rows).</summary>
    private string _leaving = "";
    private long _leaveAt;
    private string _selected = "";
    private string _painted = "";

    /// <summary>Review M2: the game section whose heading carries the game note now.</summary>
    private string _notedSection = "";

    /// <summary><c>game</c> while the Game group (and so its note) is shown; empty otherwise.</summary>
    internal string NotedSection => _notedSection;
    private readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>KEYS2: the Progenitor bind the refused key belongs to, when the two can swap keys; null otherwise.</summary>
    private KeybindClaim? _swapWith;

    /// <summary>What Save takes the pending key from (<see cref="KeybindRemapGuard.TakeFrom"/>); empty when it is free.</summary>
    private readonly List<KeybindClaim> _takeFrom = new();
    private KeybindsTabModel? _model;
    private string _editing = "";

    /// <summary>KEYS9: the slot being changed on a game row (0 primary, 1 secondary); 0 for a mod key.</summary>
    private int _editSlot;
    private KeyChord? _pending;
    private string _editStatus = "";
    private bool _capturing;
    private int _captureToken;
    private string _lastStatus = "";

    internal KeybindsPane(Func<IReadOnlyList<(string Id, string Name)>>? modOrder = null, Func<string, bool>? hasModPage = null)
    {
        _modOrder = modOrder ?? (() => Array.Empty<(string, string)>());
        _hasModPage = hasModPage ?? (_ => false);
    }

    /// <summary>Called with a mod's id when its section heading (or its Open mod page button) is clicked.</summary>
    internal Action<string>? OpenModPage { get; set; }

    /// <summary>Rebuild the pane after a save (the owner keeps the scroll position).</summary>
    internal Action? Rebuild { get; set; }

    /// <summary>Run on the next frame (the reveal needs the rows placed). Null runs now.</summary>
    internal Action<Action>? Post { get; set; }

    /// <summary>The dialog panel source for Reset all mod keys; null applies it at once (a game-free host without dialogs).</summary>
    internal Func<string, IUiPanel>? DialogFactory { get; set; }

    internal KeybindsFilter Filter { get; private set; } = KeybindsFilter.All;

    internal string Query { get; private set; } = "";

    internal KeybindsTabModel? Model => _model;

    internal string Editing => _editing;

    /// <summary>KEYS9: the game slot being changed (0 primary, 1 secondary).</summary>
    internal int EditingSlot => _editSlot;

    internal KeyChord? Pending => _pending;

    internal bool Capturing => _capturing;

    /// <summary>The row's last saved/refused sentence (the edit bar's status), for readback and tests.</summary>
    internal string LastStatus => _lastStatus;

    /// <summary>The last jump: target, the section it resolved to, and where the reveal put it.</summary>
    internal JumpRecord? LastJump { get; private set; }

    internal sealed class JumpRecord
    {
        internal string Target { get; init; } = "";
        internal string Section { get; init; } = "";
        internal bool Found { get; init; }
        internal bool Measured { get; set; }
        internal float RowTop { get; set; }
        internal float ViewportTop { get; set; }
        internal float ViewportHeight { get; set; }

        /// <summary>The heading is at the top of the viewport (or the list could not scroll further).</summary>
        internal bool Landed { get; set; }

        internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
        {
            ["target"] = Target,
            ["section"] = Section,
            ["found"] = Found,
            ["measured"] = Measured,
            ["landed"] = Landed,
            ["rowTop"] = Math.Round(RowTop, 1),
            ["viewportTop"] = Math.Round(ViewportTop, 1),
            ["viewportHeight"] = Math.Round(ViewportHeight, 1),
        };
    }

    private sealed class RowBinding
    {
        internal RowBinding(KeybindsRowModel model, KeybindsSectionModel section, IUiCellRow name, IUiCellRow? help)
        {
            Model = model;
            Section = section;
            Name = name;
            Help = help;
        }

        internal KeybindsRowModel Model { get; }
        internal KeybindsSectionModel Section { get; }
        internal IUiCellRow Name { get; }
        internal IUiCellRow? Help { get; }
        internal bool Visible { get; set; } = true;
    }

    // ---- build ------------------------------------------------------------------------------

    internal void Build(IUiPanel panel, float rowWidth)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _readback.Clear();
        _rows.Clear();
        _sections.Clear();
        _groups.Clear();
        _pageHover = "";
        _painted = "";
        // KEYS3: borrow the Bindings Menu's look (and its category titles) before anything is drawn.
        KeybindArtLadder.Prepare();
        // Opening the tab re-reads the game's keys (the Controls page may have changed them) and the cfgs.
        KeybindArbiter.Invalidate(game: true);
        _model = BuildModel();
        if (_model.FindRow(_selected) == null) _selected = "";
        if (_model.FindRow(_hovered) == null)
        {
            _hovered = "";
            _leaving = "";
        }

        UiLayoutBatch.Begin();
        try
        {
            panel.ContentAlignment = SettingsAlign.Left;
            Ui.PinMenuRows(panel, MenuRowPin.Top);
            BuildToolbar(panel);
            BuildEditBar(panel);

            // KEYS3: Reset all mod keys at the bottom right, like the game's "Reset to default key bindings".
            Ui.PinMenuRows(panel, MenuRowPin.Bottom);
            BuildFooter(panel);

            Ui.PinMenuRows(panel, MenuRowPin.Content);
            _noMatches = panel.Note(KeybindsTabLayout.NoMatches);
            _noMatches.Visible = false;
            // KEYS2: the Conflicts view's first line, while any Progenitor key is not active.
            _conflictsHeader = panel.Note(KeybindsTabLayout.ConflictsHeader(_model.InactiveCount));
            _conflictsHeader.Visible = false;
            _readback.Add((_conflictsHeader, RowConflictsHeader, (Func<object?>)ConflictsHeaderReadback));

            // Requested 2026-09-30: the mods' keys first, then the game's, each under a group header.
            for (int i = 0; i < _model.Sections.Count; i++)
            {
                var section = _model.Sections[i];
                if (!section.IsGame && FindGroup(JumpMods) == null) BuildGroup(panel, JumpMods);
                if (section.IsGame && FindGroup(JumpGame) == null) BuildGroup(panel, JumpGame);
                BuildSection(panel, section, i);
            }

            if (!_model.HasGameSections)
            {
                // No game keys: the Game group header, then the plain note that says why (never fake data).
                var group = BuildGroup(panel, JumpGame, unavailable: true);
                var note = panel.Note(_model.GameNote);
                _readback.Add((note, RowGameNote, new Dictionary<string, object?> { ["available"] = false, ["note"] = _model.GameNote }));
                _readback.Add((group.Header, RowSection, SectionReadback(null, KeybindsTabLayout.GameSectionId, KeybindsTabLayout.GameNoteHeading)));
            }
        }
        finally
        {
            UiLayoutBatch.End();
        }

        ApplyFilter();
        SyncEditBar();
        SyncCurrent();
    }

    /// <summary>
    /// KEYS3: the description column on the right (the game's details panel): the hovered or selected
    /// row's name as a heading, the game's ornament under it (the kit divider when none was borrowed),
    /// then its description, its warning sentences and its notes. <paramref name="aside"/> is the
    /// workspace's second pane (<see cref="MenuPaneMode.Aside"/>); a host without one skips this and
    /// the readback still carries the content.
    /// </summary>
    internal void BuildDescription(IUiPanel aside)
    {
        _aside = aside ?? throw new ArgumentNullException(nameof(aside));
        UiLayoutBatch.Begin();
        try
        {
            aside.ContentAlignment = SettingsAlign.Centre;
            Ui.PinMenuRows(aside, MenuRowPin.Top);
            _descHeading = aside.CellRow(new CellRowSpec(DescHeadingKey, new[]
            {
                new CellSpec(CellKind.Text, CellHeading).With(text: KeybindsTabLayout.DescriptionTitle, width: CellWidth.Fraction(1f),
                    align: CellAlign.Center, fontSize: KeybindsTabLayout.DescriptionHeadingFontSize),
            }, height: KeybindsTabLayout.CategoryRowHeight, plain: true));
            var ornament = KeybindArtLadder.Current.Ornament;
            IUiHandle ornamentRow;
            if (ornament.Borrowed && ornament.Asset != null)
            {
                var row = aside.CellRow(new CellRowSpec(DescOrnamentKey, new[]
                {
                    new CellSpec(CellKind.Image, CellOrnament).With(width: CellWidth.Fraction(0.6f), align: CellAlign.Center,
                        skin: CellSkin.Ornament),
                }, height: KeybindsTabLayout.OrnamentHeight, plain: true));
                row.SetImage(CellOrnament, ornament.Asset);
                ornamentRow = row;
            }
            else
            {
                ornamentRow = aside.Divider();
            }

            Ui.PinMenuRows(aside, MenuRowPin.Content);
            aside.ContentAlignment = SettingsAlign.Left;
            _descHint = aside.Label("", LabelTone.Normal);
            _descBody = aside.Note("");
            _descWarning = aside.Label("", LabelTone.Warn);
            _descInfo = aside.Label("", LabelTone.Muted);
            _readback.Add((_descHeading, RowDescription, (Func<object?>)DescriptionReadback));
            _readback.Add((ornamentRow, RowDescription, (Func<object?>)DescriptionReadback));
        }
        finally
        {
            UiLayoutBatch.End();
        }

        SyncDescription();
    }

    private void BuildFooter(IUiPanel panel)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "gap"),
            Sized(CellResetAll, KeybindsTabLayout.ResetAllCaption).With(align: CellAlign.Right),
        };
        var row = panel.CellRow(new CellRowSpec(FooterKey, cells, height: UnityUiRuntimeMetrics.RowHeight + 6f,
            rightInset: HeadBlockLayout.RightColumnInset, plain: true));
        _footer = row;
        row.OnClick(CellResetAll, PressResetAll);
        _readback.Add((row, RowFooter, (Func<object?>)(() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resettable"] = _model?.ResettableRows().Count ?? 0,
            ["caption"] = KeybindsTabLayout.ResetAllCaption,
        })));
    }

    /// <summary>
    /// The tab's model from the current picture; at the title screen (KEYS3 live follow-up) the game's
    /// keys come from the last in-world read.
    /// </summary>
    private KeybindsTabModel BuildModel()
    {
        var picture = KeybindArbiter.Current();
        (IReadOnlyList<KeybindClaim> Claims, DateTime SavedUtc)? cached = null;
        if (!picture.GameAvailable)
        {
            try { cached = KeybindGameCache.DisplayClaims(); }
            catch { cached = null; }
        }

        // PAD1: the controller keys come from the gamepad picture, built with this one.
        KeybindPicture? gamepad;
        try { gamepad = KeybindArbiter.CurrentGamepad(); }
        catch { gamepad = null; }
        return KeybindsTabModel.Build(picture, SafeOrder(), SafeHasPage, cached, gamepad: gamepad);
    }

    private IReadOnlyList<(string Id, string Name)> SafeOrder()
    {
        try { return _modOrder() ?? Array.Empty<(string, string)>(); }
        catch { return Array.Empty<(string, string)>(); }
    }

    private bool SafeHasPage(string id)
    {
        try { return _hasModPage(id); }
        catch { return false; }
    }

    private void BuildToolbar(IUiPanel panel)
    {
        var model = _model!;
        // Requested 2026-09-30: three visibly separate groups after the search (the filter, the
        // section jumps, the mod picker), built in KeybindsTabLayout.ToolbarCells.
        var cells = KeybindsTabLayout.ToolbarCells(Query, Filter, model.ConflictCount, _currentJump);
        var row = panel.CellRow(new CellRowSpec(ToolbarKey, cells, height: UnityUiRuntimeMetrics.RowHeight + 6f,
            rightInset: HeadBlockLayout.RightColumnInset));
        _toolbar = row;
        row.OnChanged(CellSearch, text => ApplySearch(text));
        row.OnClick(CellAll, () => SetFilter(KeybindsFilter.All));
        row.OnClick(CellConflicts, () => SetFilter(KeybindsFilter.Conflicts));
        row.OnClick(CellChanged, () => SetFilter(KeybindsFilter.Changed));
        row.OnClick(CellGameJump, () => JumpTo(JumpGame));
        row.OnClick(CellModJump, () => JumpTo(JumpMods));
        row.OnClick(CellJumpToMod, PressJumpToMod);
        _readback.Add((row, RowToolbar, (Func<object?>)ToolbarReadback));
        SyncToolbarVisibility();
    }

    /// <summary>Requested 2026-09-30: the section the tab is on (<see cref="JumpMods"/> by default, or <see cref="JumpGame"/>); its jump is drawn bright.</summary>
    internal string CurrentJump => _currentJump;

    private string _currentJump = JumpMods;

    private void SetCurrentJump(string jump)
    {
        _currentJump = jump;
        if (_toolbar is not { IsDisposed: false } t) return;
        t.SetCell(CellModJump, tone: string.Equals(jump, JumpMods, StringComparison.Ordinal) ? CellTone.Normal : CellTone.Muted);
        t.SetCell(CellGameJump, tone: string.Equals(jump, JumpGame, StringComparison.Ordinal) ? CellTone.Normal : CellTone.Muted);
    }

    private static CellSpec Droppable(CellSpec cell, int order)
    {
        cell.Optional = true;
        cell.DropOrder = order;
        return cell;
    }

    /// <summary>KEYS3 polish: the toolbar's cells, for the geometry check (the live row's specs).</summary>
    internal IReadOnlyList<CellSpec> ToolbarCells => _toolbar?.Cells ?? Array.Empty<CellSpec>();

    /// <summary>A button sized to its caption (live check 2026-09-29: equal widths cut "Reset all mod keys").</summary>
    private static CellSpec Sized(string key, string caption) =>
        new CellSpec(CellKind.Button, key).With(text: caption, width: CellWidth.Px(KeybindsTabLayout.ButtonWidth(caption)));

    /// <summary>
    /// <b>Jump to mod…</b> on the toolbar (live check 2026-09-29: the separate selector row left a dead band
    /// under the toolbar): the framework's choose dialog lists the mods that have keys; a pick jumps there.
    /// </summary>
    internal void PressJumpToMod()
    {
        var targets = ModJumpTargets();
        if (targets.Count == 0) return;
        if (targets.Count == 1 || DialogFactory == null)
        {
            JumpTo(targets[0].Id);
            return;
        }

        var shown = targets.Count > DialogLayout.MaxListOptions ? targets.GetRange(0, DialogLayout.MaxListOptions) : targets;
        var names = new List<string>(shown.Count);
        foreach (var (_, title) in shown) names.Add(title);
        try
        {
            UiDialogs.Choose(this, SettingsPanel.PluginId, Ui.FrameworkFooter, KeybindsTabLayout.JumpToModCaption, "",
                names, i => { if (i >= 0 && i < shown.Count) JumpTo(shown[i].Id); },
                KeybindsTabLayout.CancelCaption, null);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds jump-to-mod dialog threw: " + e.Message); }
    }

    private void BuildEditBar(IUiPanel panel)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellEditText).With(text: "", width: CellWidth.Fraction(0.55f), wrap: CellWrap.Ellipsis),
            new CellSpec(CellKind.Button, CellSwap).With(text: KeybindsTabLayout.SwapCaption, align: CellAlign.Right),
            new CellSpec(CellKind.Button, CellSave).With(text: KeybindsTabLayout.SaveCaption, align: CellAlign.Right),
            new CellSpec(CellKind.Button, CellUnbind).With(text: KeybindsTabLayout.UnbindCaption, align: CellAlign.Right),
            new CellSpec(CellKind.Button, CellCancel).With(text: KeybindsTabLayout.CancelCaption, align: CellAlign.Right),
        };
        var row = panel.CellRow(new CellRowSpec(EditKey, cells, height: UnityUiRuntimeMetrics.RowHeight + 6f,
            rightInset: HeadBlockLayout.RightColumnInset));
        _edit = row;
        row.OnClick(CellSave, SaveEdit);
        row.OnClick(CellSwap, PressSwap);
        row.OnClick(CellUnbind, UnbindEdit);
        row.OnClick(CellCancel, CancelEdit);
        _readback.Add((row, RowEdit, (Func<object?>)EditReadback));
    }

    /// <summary>
    /// A section heading. KEYS3: the category (or the mod's name) in the game's larger heading size at
    /// the left; for a mod, <b>Open mod page</b> and the key count at the right.
    /// </summary>
    private CellRowSpec SectionSpec(int index, string title, string count, bool withPageLink, bool game)
    {
        var cells = new List<CellSpec>();
        if (withPageLink)
        {
            // Requested 2026-09-30: "Open mod page" is a small icon right after the mod's name. The
            // title is as wide as its caption, the icon follows it, and the count stays at the right.
            // KEYSHEAD1 (live 2026.10.136: "My Mod" drew as "My M… »"): the runtime sizes the title to its
            // caption measured with the drawn font; the estimate with the kit's slack is only its fallback.
            var titleCell = new CellSpec(CellKind.Text, CellTitle).With(text: title ?? "",
                width: CellWidth.Px(KeybindsTabLayout.HeadingTitleWidth(title ?? "")), fontSize: KeybindsTabLayout.CategoryFontSize,
                vAlign: CellVAlign.Bottom, tone: CellTone.Normal,
                minWidth: KeybindsTabLayout.HeadingTitleWidth(title ?? ""));
            titleCell.FitCaption = true;
            cells.Add(titleCell);
            // Live 2026-09-30: a plain inline glyph, no button chrome, muted; hovering the heading (the
            // whole heading is the link: a click anywhere opens the page) brightens it.
            float glyph = CellLayout.EstimateTextWidth(KeybindsTabLayout.OpenModPageGlyph, KeybindsTabLayout.CategoryFontSize) + 6f;
            cells.Add(new CellSpec(CellKind.Text, CellOpenPage).With(text: KeybindsTabLayout.OpenModPageGlyph,
                width: CellWidth.Px(glyph), minWidth: glyph, vAlign: CellVAlign.Bottom,
                fontSize: KeybindsTabLayout.CategoryFontSize, tone: CellTone.Muted));
            cells.Add(new CellSpec(CellKind.Spacer, "gap"));
        }
        else
        {
            // KEYS3 live K13: an Auto width is the caption's *estimate* at the heading size, and "Movement"
            // drew wider than its estimate ("Movem…"). The title takes the whole row left of the cells the
            // heading actually has (the count), so a heading is never cut.
            cells.Add(new CellSpec(CellKind.Text, CellTitle).With(text: title ?? "", width: CellWidth.Fill(KeybindsTabLayout.MinNameWidth),
                fontSize: KeybindsTabLayout.CategoryFontSize, vAlign: CellVAlign.Bottom, tone: CellTone.Normal,
                minWidth: KeybindsTabLayout.MinNameWidth));
        }

        if (!game)
            cells.Add(new CellSpec(CellKind.Muted, CellCount).With(text: count, align: CellAlign.Right,
                fontSize: KeybindsTabLayout.SectionFontSize, vAlign: CellVAlign.Bottom, tone: CellTone.Muted));
        return new CellRowSpec(
            SectionKeyPrefix + (index < 0 ? "note" : index.ToString(CultureInfo.InvariantCulture)), cells,
            height: game ? KeybindsTabLayout.CategoryRowHeight : KeybindsTabLayout.ModHeadingHeight,
            rightInset: HeadBlockLayout.RightColumnInset, plain: !withPageLink, hover: withPageLink, skin: CellRowSkin.CategoryHeading);
    }

    // ---- group headers: "Mod keys" and "Game keys" (requested 2026-09-30) ------------------

    internal const string GroupKeyPrefix = "keys-group-";
    internal const string GroupDividerKeyPrefix = "keys-group-divider-";
    internal const string RowGroup = "keys-group";
    internal const string CellGroupTitle = "group-title";
    internal const string CellGroupDivider = "group-divider";
    internal const string GroupSpaceKeyPrefix = "keys-group-space-";
    internal const string CellGroupSpace = "group-space";

    private sealed class Group
    {
        internal Group(string id, IUiCellRow header, IUiHandle divider, IUiHandle? note, IUiHandle space)
        {
            Space = space;
            Id = id;
            Header = header;
            Divider = divider;
            Note = note;
        }

        internal string Id { get; }
        internal IUiCellRow Header { get; }
        internal IUiHandle Divider { get; }
        internal IUiHandle? Note { get; }

        /// <summary>The row of space above the header.</summary>
        internal IUiHandle Space { get; }
    }

    private readonly List<Group> _groups = new();

    private Group? FindGroup(string id)
    {
        foreach (var g in _groups)
            if (string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) return g;
        return null;
    }

    /// <summary>
    /// A group header: a larger title with space above it (<see cref="KeybindsTabLayout.GroupHeaderHeight"/>),
    /// then a full-width divider — the game's own ornament when it was borrowed, else the kit divider —
    /// and, for the Game group, its muted note (where the game's keys are changed).
    /// </summary>
    private Group BuildGroup(IUiPanel panel, string id, bool unavailable = false)
    {
        bool game = string.Equals(id, JumpGame, StringComparison.Ordinal);
        string title = game ? KeybindsTabLayout.GameGroupTitle : KeybindsTabLayout.ModGroupTitle;
        // The space above the group is a row of its own, and the title fills the next row (live
        // 2026-09-30: a bottom-aligned 28 px title in one tall row was not drawn at all).
        var space = panel.CellRow(new CellRowSpec(GroupSpaceKeyPrefix + id, new[]
        {
            new CellSpec(CellKind.Muted, CellGroupSpace).With(text: "", width: CellWidth.Fraction(1f)),
        }, height: KeybindsTabLayout.GroupSpaceAbove, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
        var header = panel.CellRow(new CellRowSpec(GroupKeyPrefix + id, new[]
        {
            new CellSpec(CellKind.Text, CellGroupTitle).With(text: title, width: CellWidth.Fill(KeybindsTabLayout.MinNameWidth),
                fontSize: KeybindsTabLayout.GroupFontSize, tone: CellTone.Normal),
        }, height: KeybindsTabLayout.GroupHeaderHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
        IUiHandle divider;
        var ornament = KeybindArtLadder.Current.Ornament;
        if (ornament.Borrowed && ornament.Asset != null)
        {
            var row = panel.CellRow(new CellRowSpec(GroupDividerKeyPrefix + id, new[]
            {
                new CellSpec(CellKind.Image, CellGroupDivider).With(width: CellWidth.Fraction(1f), skin: CellSkin.Ornament),
            }, height: KeybindsTabLayout.OrnamentHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
            row.SetImage(CellGroupDivider, ornament.Asset);
            divider = row;
        }
        else
        {
            divider = panel.Divider();
        }

        IUiHandle? note = null;
        if (game && !unavailable)
        {
            note = panel.Note(GameHeadingNote);
            _readback.Add((note, RowGameNote, (Func<object?>)(() => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["available"] = true,
                ["note"] = GameHeadingNote,
                ["fromCache"] = _model?.GameFromCache ?? false,
                ["shown"] = _notedSection.Length > 0,
                ["opener"] = "words",
            })));
        }

        var group = new Group(id, header, divider, note, space);
        _groups.Add(group);
        string gid = id;
        _readback.Add((header, RowGroup, (Func<object?>)(() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = gid,
            ["title"] = title,
            ["divider"] = ornament.Borrowed && ornament.Asset != null ? "ornament" : "kit",
            ["visible"] = !header.IsDisposed && header.Visible,
        })));
        return group;
    }

    /// <summary>The group header row (<see cref="JumpMods"/> or <see cref="JumpGame"/>), for tests and jumps.</summary>
    internal IUiCellRow? GroupHeaderOf(string id) => FindGroup(id)?.Header;

    // ---- the heading's page icon hover ----------------------------------------------------------

    private string _pageHover = "";
    private string _pageHoverTitle = "";

    /// <summary>The pointer entered or left a mod heading's page icon: the description column says what it does.</summary>
    internal void HoverOpenPage(string ownerId, string title, bool entered)
    {
        if (entered)
        {
            _pageHover = ownerId ?? "";
            _pageHoverTitle = title ?? "";
        }
        else if (string.Equals(_pageHover, ownerId, StringComparison.OrdinalIgnoreCase))
        {
            _pageHover = "";
            _pageHoverTitle = "";
        }

        SyncDescription();
    }

    /// <summary>The note on the first game heading: the read-only note, after the snapshot's age at the title.</summary>
    internal string GameHeadingNote => KeybindsTabLayout.GameHeadingNote(_model?.GameCacheNote ?? "");

    private void BuildSection(IUiPanel panel, KeybindsSectionModel section, int index)
    {
        bool link = !section.IsGame && section.HasModPage;
        var heading = panel.CellRow(SectionSpec(index, section.Title, section.CountText, link, section.IsGame));
        IUiHandle? note = null;
        // Keybind groups: a group that can be unavailable (a server setting) keeps a note under its
        // heading, shown with the reason while it is (ApplyFilter); the rows stay and can still be changed.
        if (section.CanBeUnavailable)
        {
            note = panel.Note(section.UnavailableReason ?? "");
            note.Visible = false;
        }

        if (link)
        {
            string owner = section.OwnerId;
            string title = section.Title;
            // The mod's name is clickable too: a click anywhere on the heading row opens its page.
            heading.OnRowClick(() => OpenModPage?.Invoke(owner));
            var h = heading;
            heading.OnHover(entered =>
            {
                if (!h.IsDisposed) h.SetCell(CellOpenPage, tone: entered ? CellTone.Normal : CellTone.Muted);
                HoverOpenPage(owner, title, entered);
            });
        }

        _sections.Add((section, heading, note));
        var s = section;
        _readback.Add((heading, RowSection, (Func<object?>)(() => SectionReadback(s, s.Id, s.Title))));
        foreach (var row in section.Rows)
            BuildRow(panel, section, row);
    }

    private void BuildRow(IUiPanel panel, KeybindsSectionModel section, KeybindsRowModel row)
    {
        int n = _rows.Count;
        // KEYS3: one line per key, as the game's Bindings Menu: the help and notes are in the
        // description column, not under the name.
        var name = panel.CellRow(new CellRowSpec(
            NameRowPrefix + n.ToString(CultureInfo.InvariantCulture), KeybindsTabLayout.RowCells(row, row.PrimaryText, current: false),
            height: KeybindsTabLayout.RowHeight, rightInset: HeadBlockLayout.RightColumnInset,
            hover: true, skin: CellRowSkin.GameEntry));
        var binding = new RowBinding(row, section, name, null);
        _rows.Add(binding);
        string id = row.Id;
        name.OnHover(entered => Hover(id, entered));
        name.OnRowClick(() => Select(id));
        name.OnClick(CellKey, () => PressPrimary(id));
        name.OnClick(CellKey2, () => PressSecondary(id));
        if (row.HasGutter)
        {
            name.OnClick(CellReset, () => ResetRow(id));
            name.OnClick(CellClear, () => ClearRow(id));
            // KEYS3 polish: hovering reset or Unbind says what it does (or why it is dimmed).
            name.OnCellHover(CellReset, entered => HoverCell(id, CellReset, entered));
            name.OnCellHover(CellClear, entered => HoverCell(id, CellClear, entered));
        }

        var b = binding;
        _readback.Add((name, RowKey, (Func<object?>)(() => RowReadback(b))));
        SyncRow(row.Id);
    }

    // ---- KEYS3: hover, selection and the description column ----------------------------------

    /// <summary>
    /// The row the description column shows: the one being changed (user report 2026-10-10: hovering another row
    /// hid the edit's warning), else the hovered one, else the selected one.
    /// </summary>
    internal string CurrentRow => _editing.Length > 0 ? _editing : _hovered.Length > 0 ? _hovered : _selected;

    internal string HoveredRow => _hovered;

    /// <summary>The cell-row key of a key row (its <c>keys-row-N</c>), for the bridge's pointer ops; empty when unknown.</summary>
    internal string NameRowKeyOf(string rowId)
    {
        foreach (var r in _rows)
            if (string.Equals(r.Model.Id, rowId, StringComparison.OrdinalIgnoreCase)) return r.Name.Key;
        return "";
    }

    /// <summary>Before <see cref="Build"/>: the row to show selected (kept across a rebuild; dropped if it is gone).</summary>
    internal void Preselect(string? rowId) => _selected = rowId ?? "";

    internal string SelectedRow => _selected;

    /// <summary>The rows' notes (what a take moved), carried into the next pane by a rebuild after a save.</summary>
    internal IReadOnlyDictionary<string, string> Notes => _notes;

    /// <summary>Before <see cref="Build"/>: the notes an earlier pane showed (a rebuild after a save).</summary>
    internal void KeepNotes(IReadOnlyDictionary<string, string>? notes)
    {
        if (notes == null) return;
        foreach (var kv in notes) _notes[kv.Key] = kv.Value;
    }

    /// <summary>
    /// How long a row the pointer left stays described (user report 2026-10-10: crossing the gap between two rows
    /// flashed the selected row's description before the next row's). Zero clears at once.
    /// </summary>
    internal static int HoverLeaveGraceMs { get; set; } = 150;

    /// <summary>The clock for <see cref="HoverLeaveGraceMs"/>, in milliseconds; a seam for tests.</summary>
    internal static Func<long> ClockMs { get; set; } = () => Environment.TickCount64;

    /// <summary>The pointer entered or left a key row (its row trigger), or the bridge's <c>hover</c>.</summary>
    internal void Hover(string rowId, bool entered)
    {
        string id = rowId ?? "";
        if (entered)
        {
            _hovered = id;
            _leaving = "";
        }
        else if (string.Equals(_hovered, id, StringComparison.OrdinalIgnoreCase))
        {
            // The next row's enter usually arrives within the grace; only a pointer that left the list clears.
            _hoverCell = "";
            if (HoverLeaveGraceMs > 0)
            {
                _leaving = id;
                _leaveAt = ClockMs() + HoverLeaveGraceMs;
            }
            else
            {
                _hovered = "";
            }
        }

        SyncCurrent();
    }

    /// <summary>From the tab's per-frame tick: the left row stops being described once its grace is over.</summary>
    internal void Tick()
    {
        if (_leaving.Length > 0 && ClockMs() >= _leaveAt) FlushHoverLeave();
    }

    /// <summary>End a pending hover leave now (the tick after its grace, or the bridge's leave).</summary>
    internal void FlushHoverLeave()
    {
        if (_leaving.Length == 0) return;
        if (string.Equals(_hovered, _leaving, StringComparison.OrdinalIgnoreCase))
        {
            _hovered = "";
            _hoverCell = "";
        }

        _leaving = "";
        SyncCurrent();
    }

    /// <summary>The gutter button under the pointer (<see cref="CellReset"/> / <see cref="CellClear"/>) of <see cref="HoveredRow"/>, or empty.</summary>
    internal string HoveredCell => _hoverCell;

    /// <summary>KEYS3 polish: the pointer entered or left a gutter button of <paramref name="rowId"/>.</summary>
    internal void HoverCell(string rowId, string cell, bool entered)
    {
        if (entered)
        {
            _hovered = rowId ?? "";
            _hoverCell = cell ?? "";
            _leaving = "";
        }
        else if (string.Equals(_hoverCell, cell, StringComparison.Ordinal) && string.Equals(_hovered, rowId, StringComparison.OrdinalIgnoreCase))
        {
            _hoverCell = "";
        }

        SyncCurrent();
    }

    /// <summary>A click on a key row (outside its caps and gutter): it stays described after the pointer leaves.</summary>
    internal void Select(string rowId)
    {
        if (_model?.FindRow(rowId) == null) return;
        _selected = _model.FindRow(rowId)!.Id;
        SyncCurrent();
    }

    /// <summary>
    /// The primary key cap. A mod key starts the KEYS2 capture (every refusal and the swap unchanged); a
    /// read-only mod key says why. KEYS9: a game key on the game's Bindings Menu starts the same capture for
    /// its primary slot, in a world; otherwise the click selects the row and puts why it cannot be changed
    /// (<see cref="GameKeyRemap.NotEditableNow"/>) in the description column.
    /// </summary>
    internal void PressPrimary(string rowId)
    {
        var row = _model?.FindRow(rowId);
        if (row == null) return;
        _selected = row.Id;
        if (row.IsGame)
        {
            PressGameCap(row, 0);
            return;
        }

        if (!row.Editable)
        {
            ShowNote(row.Id, row.Claim.NotEditableReason);
            return;
        }

        BeginEdit(row.Id);
        SyncCurrent();
    }

    /// <summary>
    /// The secondary cap: a game row's second slot (KEYS9); PAD1: a Progenitor bind's controller key, which starts the
    /// controller capture.
    /// </summary>
    internal void PressSecondary(string rowId)
    {
        var row = _model?.FindRow(rowId);
        if (row == null || !row.SecondaryEnabled) return;
        _selected = row.Id;
        if (row.HasGamepadCap)
        {
            BeginEdit(row.Id, GamepadSlot);
            SyncCurrent();
            return;
        }

        PressGameCap(row, 1);
    }

    /// <summary>PAD1: the edit slot of a Progenitor bind's controller key.</summary>
    internal const int GamepadSlot = 2;

    /// <summary>PAD1: the key being changed is a controller key.</summary>
    private bool EditingGamepad => _editSlot == GamepadSlot;

    /// <summary>KEYS9: a game cap's click: the capture for that slot, or the note saying why it cannot change.</summary>
    private void PressGameCap(KeybindsRowModel row, int slot)
    {
        string why = row.Editable ? GameKeyRemap.NotEditableNow(row.Claim) : GameKeyRemap.Writer == null ? KeybindsTabLayout.GameCapNote : GameKeyRemap.OnlyInGameMenuReason;
        if (why.Length == 0 && !row.SlotClaim(slot).GameSlotExists) why = GameKeyRemap.NoSlotReason;
        if (why.Length > 0)
        {
            ShowNote(row.Id, why);
            return;
        }

        BeginEdit(row.Id, slot);
        SyncCurrent();
    }

    private void ShowNote(string rowId, string note)
    {
        if (!string.IsNullOrWhiteSpace(note)) _notes[rowId] = note;
        _lastStatus = note ?? "";
        SyncCurrent();
    }

    /// <summary>What the description column shows now (<see cref="KeybindsTabLayout.Describe"/>).</summary>
    internal KeybindsDescription Description
    {
        get
        {
            if (_pageHover.Length > 0)
                return new KeybindsDescription("", _pageHoverTitle, "", "", "", "", KeybindsDescription.SourceHover,
                    KeybindsTabLayout.OpenModPageHint);
            string id = CurrentRow;
            var row = id.Length == 0 ? null : _model?.FindRow(id);
            if (row == null) return KeybindsDescription.Empty;
            bool editing = string.Equals(_editing, row.Id, StringComparison.OrdinalIgnoreCase);
            string source = !editing && _hovered.Length > 0 ? KeybindsDescription.SourceHover : KeybindsDescription.SourceSelected;
            _notes.TryGetValue(row.Id, out string? note);
            string hint = _hoverCell.Length > 0 && string.Equals(_hovered, row.Id, StringComparison.OrdinalIgnoreCase)
                ? KeybindsTabLayout.GutterHint(row, _hoverCell, editing) : "";
            return KeybindsTabLayout.Describe(row, source, editing ? _editStatus : "", note ?? "", hint, warnStatus: editing && _takeFrom.Count > 0);
        }
    }

    /// <summary>Highlight the current row (the game's band), brighten its name, and rewrite the description column.</summary>
    private void SyncCurrent()
    {
        string current = CurrentRow;
        if (!string.Equals(current, _painted, StringComparison.OrdinalIgnoreCase))
        {
            string was = _painted;
            _painted = current;
            PaintCurrent(was, false);
            PaintCurrent(current, true);
        }

        SyncDescription();
    }

    private void PaintCurrent(string rowId, bool on)
    {
        if (string.IsNullOrEmpty(rowId)) return;
        foreach (var r in _rows)
        {
            if (!string.Equals(r.Model.Id, rowId, StringComparison.OrdinalIgnoreCase) || r.Name.IsDisposed) continue;
            var model = _model?.FindRow(rowId) ?? r.Model;
            try
            {
                r.Name.SetSelected(on);
                r.Name.SetCell(CellName, tone: model.Loses || model.ReservedText.Length > 0 ? CellTone.Warn
                    : on ? CellTone.Normal : CellTone.Muted);
            }
            catch { /* a torn-down row */ }
        }
    }

    private void SyncDescription()
    {
        var d = Description;
        if (_descHeading is { IsDisposed: false } h) h.SetCell(CellHeading, text: d.Heading);
        if (_descHint is { IsDisposed: false } hint)
        {
            hint.SetText(d.Hint, LabelTone.Normal);
            hint.Visible = d.Hint.Length > 0;
        }

        if (_descBody is { IsDisposed: false } body)
        {
            body.Text = d.Body;
            body.Visible = d.Body.Length > 0;
        }

        if (_descWarning is { IsDisposed: false } warning)
        {
            warning.SetText(d.Warning, d.WarningTone == "warn" ? LabelTone.Warn : LabelTone.Muted);
            warning.Visible = d.Warning.Length > 0;
        }

        if (_descInfo is { IsDisposed: false } info)
        {
            info.SetText(d.Info, LabelTone.Muted);
            info.Visible = d.Info.Length > 0;
        }
    }

    private object? DescriptionReadback()
    {
        var d = Description.Readback();
        d["hovered"] = _hovered;
        d["selected"] = _selected;
        d["ornament"] = KeybindArtLadder.Current.Ornament.Readback();
        return d;
    }

    // ---- filter, search ---------------------------------------------------------------------

    internal void SetFilter(KeybindsFilter filter)
    {
        Filter = filter;
        if (_toolbar is { IsDisposed: false } t)
        {
            // One three-way choice: the chosen segment in the kit's selected accent, the others muted.
            t.SetCell(CellAll, tone: filter == KeybindsFilter.All ? CellTone.Gold : CellTone.Muted);
            t.SetCell(CellConflicts, tone: filter == KeybindsFilter.Conflicts ? CellTone.Gold : CellTone.Muted);
            t.SetCell(CellChanged, tone: filter == KeybindsFilter.Changed ? CellTone.Gold : CellTone.Muted);
        }

        ApplyFilter();
    }

    internal void ApplySearch(string? text)
    {
        string next = (text ?? "").Trim();
        if (string.Equals(next, Query, StringComparison.Ordinal)) return;
        Query = next;
        ApplyFilter();
    }

    /// <summary>Hide and show the rows already drawn (nothing is rebuilt), and headings with nothing under them.</summary>
    private void ApplyFilter()
    {
        int shown = 0;
        foreach (var r in _rows)
        {
            bool v = KeybindsTabModel.RowVisible(r.Model, r.Section, Filter, Query);
            r.Visible = v;
            SetVisible(r.Name, v);
            SetVisible(r.Help, v);
            if (v) shown++;
        }

        bool anyMod = false, anyGame = false;
        foreach (var (section, heading, note) in _sections)
        {
            bool any = KeybindsTabModel.VisibleRows(section, Filter, Query) > 0;
            SetVisible(heading, any);
            if (section.IsGroup) SyncGroupNote(section, note, any);
            else SetVisible(note, any);
            if (section.IsGame) anyGame |= any;
            else anyMod |= any;
        }

        // A group header, its divider and (Game) its note show while any of its sections is shown (review
        // M2: the Game note never leaves with Movement). The unavailable Game group always shows its note.
        foreach (var g in _groups)
        {
            bool game = string.Equals(g.Id, JumpGame, StringComparison.Ordinal);
            bool on = game ? anyGame || !(_model?.HasGameSections ?? false) : anyMod;
            SetVisible(g.Space, on);
            SetVisible(g.Header, on);
            SetVisible(g.Divider, on);
            SetVisible(g.Note, on);
        }

        _notedSection = anyGame ? JumpGame : "";

        if (_noMatches is { IsDisposed: false } nm) nm.Visible = shown == 0 && _rows.Count > 0;
        if (_conflictsHeader is { IsDisposed: false } ch) ch.Visible = ConflictsHeaderText.Length > 0;
    }

    /// <summary>A group's unavailable note: the reason read now, shown while the section is and the reason stands.</summary>
    private static void SyncGroupNote(KeybindsSectionModel section, IUiHandle? note, bool sectionShown)
    {
        if (note == null || note.IsDisposed) return;
        string? reason = section.UnavailableReason;
        if (reason != null && !string.Equals(note.Text, reason, StringComparison.Ordinal)) note.Text = reason;
        note.Visible = sectionShown && reason != null;
    }

    /// <summary>The unavailable note under a group's heading as drawn now (empty when hidden or none), for the readback.</summary>
    private string GroupNoteShown(KeybindsSectionModel section)
    {
        foreach (var (s, _, note) in _sections)
            if (ReferenceEquals(s, section))
                return note is { IsDisposed: false, Visible: true } n ? n.Text : "";
        return "";
    }

    /// <summary>KEYS2: the Conflicts view's header line as shown now; empty when hidden.</summary>
    internal string ConflictsHeaderText =>
        Filter == KeybindsFilter.Conflicts && _model != null ? KeybindsTabLayout.ConflictsHeader(_model.InactiveCount) : "";

    private object? ConflictsHeaderReadback() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["text"] = ConflictsHeaderText,
        ["inactive"] = _model?.InactiveCount ?? 0,
        ["visible"] = ConflictsHeaderText.Length > 0,
    };

    private static void SetVisible(IUiHandle? handle, bool visible)
    {
        if (handle == null || handle.IsDisposed) return;
        try { handle.Visible = visible; }
        catch { /* a torn-down widget */ }
    }

    // ---- jumps ------------------------------------------------------------------------------

    /// <summary>
    /// Scroll to <paramref name="target"/>: <c>game</c> (the Game section, or the top), <c>mods</c> (the
    /// first mod section) or <c>mod:&lt;id&gt;</c>. The heading goes to the top of the viewport
    /// (<see cref="UiReveal"/>: the heading down to the last row is the block, so it scrolls unless
    /// everything from the heading down already fits). Measured on the next frame, after the layout.
    /// </summary>
    /// <summary>
    /// Requested 2026-09-30: the tab's default focus. Lands on the Mod keys group (its jump is the
    /// current one) without clearing a search or filter the player left on the tab; with no mod keys
    /// shown, the list stays at its top.
    /// </summary>
    internal bool OpenOnDefault()
    {
        SetCurrentJump(JumpMods);
        var group = FindGroup(JumpMods);
        if (group == null || group.Header.IsDisposed || !group.Header.Visible) return false;
        return JumpTo(JumpMods);
    }

    /// <summary>KEYS5: told after every jump that landed (a link, a section jump, Jump to mod, the default).</summary>
    internal Action<string>? Jumped { get; set; }

    internal bool JumpTo(string target)
    {
        bool landed = JumpToCore(target);
        if (landed)
        {
            try { Jumped?.Invoke((target ?? "").Trim()); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds jump hook threw: " + e.Message); }
        }

        return landed;
    }

    private bool JumpToCore(string target)
    {
        string t = (target ?? "").Trim();
        IUiHandle? heading = null;
        string section = "";
        // Review M2: "game" and "mods" land on the first matching section still shown; only when none is
        // shown does the jump fall back to the first one (and clear the search or filter below).
        IUiHandle? firstHidden = null;
        string firstHiddenId = "";
        foreach (var (s, h, _) in _sections)
        {
            bool match = t.Equals(JumpGame, StringComparison.OrdinalIgnoreCase)
                ? s.IsGame
                : t.Equals(JumpMods, StringComparison.OrdinalIgnoreCase)
                    ? !s.IsGame
                    : string.Equals(s.Id, t, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            if (!h.Visible)
            {
                if (firstHidden == null) { firstHidden = h; firstHiddenId = s.Id; }
                continue;
            }

            heading = h;
            section = s.Id;
            break;
        }

        // Keybind groups: a mod whose every key is in its groups has no section of its own; its first group stands in.
        if (heading == null && firstHidden == null && t.StartsWith(KeybindsTabLayout.ModSectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string owner = t.Substring(KeybindsTabLayout.ModSectionPrefix.Length);
            foreach (var (s, h, _) in _sections)
            {
                if (!s.IsGroup || !string.Equals(s.OwnerId, owner, StringComparison.OrdinalIgnoreCase)) continue;
                if (h.Visible) { heading = h; section = s.Id; break; }
                if (firstHidden == null) { firstHidden = h; firstHiddenId = s.Id; }
            }
        }

        if (heading == null && firstHidden != null)
        {
            heading = firstHidden;
            section = firstHiddenId;
        }

        // A filter can hide the section: clear it so the jump shows what it names.
        if (heading != null && !heading.Visible && (Filter != KeybindsFilter.All || Query.Length > 0))
        {
            Query = "";
            if (_toolbar is { IsDisposed: false } tb) tb.SetCell(CellSearch, text: "");
            SetFilter(KeybindsFilter.All);
        }

        // Requested 2026-09-30: "game" and "mods" land on their group header, so the group's title shows.
        bool groupJump = t.Equals(JumpGame, StringComparison.OrdinalIgnoreCase) || t.Equals(JumpMods, StringComparison.OrdinalIgnoreCase);
        if (heading != null && groupJump && FindGroup(t.Equals(JumpGame, StringComparison.OrdinalIgnoreCase) ? JumpGame : JumpMods) is { } g
            && !g.Header.IsDisposed)
            heading = g.Header;
        if (heading != null)
            SetCurrentJump(section.StartsWith(KeybindsTabLayout.GameSectionPrefix, StringComparison.OrdinalIgnoreCase) ? JumpGame : JumpMods);

        var record = new JumpRecord { Target = t, Section = section, Found = heading != null };
        LastJump = record;
        if (heading == null)
        {
            Plugin.Logger?.LogInfo("Ui: keybinds jump to '" + t + "': no such section.");
            return false;
        }

        var panel = _panel;
        var last = LastHandle() ?? heading;
        void Reveal()
        {
            if (!ReferenceEquals(panel, _panel) || heading.IsDisposed) return;
            if (UiReveal.TryReveal(panel, heading, last, out float rowTop, out float viewportTop, out float viewportHeight))
            {
                record.Measured = true;
                record.RowTop = rowTop;
                record.ViewportTop = viewportTop;
                record.ViewportHeight = viewportHeight;
                record.Landed = Math.Abs(rowTop - viewportTop) < 1f || viewportTop < rowTop;
            }
        }

        if (Post != null) Post(Reveal);
        else Reveal();
        return true;
    }

    private IUiHandle? LastHandle()
    {
        for (int i = _rows.Count - 1; i >= 0; i--)
            return (IUiHandle?)_rows[i].Help ?? _rows[i].Name;
        return null;
    }

    private List<(string Id, string Title)> ModJumpTargets()
    {
        var list = new List<(string, string)>();
        if (_model == null) return list;
        // Keybind groups: a group section is a target of its own (its id, not its owner's).
        foreach (var s in _model.Sections)
            if (!s.IsGame) list.Add((s.Id, s.Title));
        return list;
    }

    private List<object?> JumpTargetsReadback()
    {
        var list = new List<object?>();
        if (_model == null) return list;
        // Requested 2026-09-30: the mods' keys come first, so their jump does too.
        if (ModJumpTargets().Count > 0) list.Add(new Dictionary<string, object?> { ["target"] = JumpMods, ["label"] = KeybindsTabLayout.ModJumpCaption });
        if (_model.HasGameSections) list.Add(new Dictionary<string, object?> { ["target"] = JumpGame, ["label"] = KeybindsTabLayout.GameJumpCaption });
        foreach (var (id, title) in ModJumpTargets())
            list.Add(new Dictionary<string, object?> { ["target"] = id, ["label"] = title });
        return list;
    }

    /// <summary>The section heading the jump targets, for tests: game, mods, or mod:&lt;id&gt;.</summary>
    internal IUiHandle? HeadingOf(string sectionId)
    {
        // "game" and "mods" are their group headers (what the section jumps reveal).
        if (string.Equals(sectionId, JumpGame, StringComparison.OrdinalIgnoreCase) || string.Equals(sectionId, JumpMods, StringComparison.OrdinalIgnoreCase))
        {
            var group = FindGroup(string.Equals(sectionId, JumpGame, StringComparison.OrdinalIgnoreCase) ? JumpGame : JumpMods);
            if (group != null) return group.Header;
        }

        bool game = string.Equals(sectionId, JumpGame, StringComparison.OrdinalIgnoreCase);
        foreach (var (s, h, _) in _sections)
            if (game ? s.IsGame : string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase)) return h;
        return null;
    }

    private void SyncToolbarVisibility()
    {
        var t = _toolbar;
        if (t == null || _model == null) return;
        t.SetCell(CellGameJump, visible: _model.HasGameSections);
        t.SetCell(CellModJump, visible: ModJumpTargets().Count > 0);
    }

    // ---- the remap flow ---------------------------------------------------------------------

    /// <summary>Start changing a mod key: the next chord pressed is read, not stored.</summary>
    internal bool BeginEdit(string rowId) => BeginEdit(rowId, -1);

    /// <summary>
    /// Start changing a key. <paramref name="slot"/> is a game row's slot (KEYS9); -1 takes it from the id,
    /// where a game row's secondary claim key (<c>game:game/Interact#2</c>) names the second slot.
    /// </summary>
    internal bool BeginEdit(string rowId, int slot)
    {
        var row = _model?.FindRow(rowId);
        if (row == null && slot < 0 && (rowId ?? "").EndsWith("#2", StringComparison.Ordinal))
        {
            row = _model?.FindRow(rowId!.Substring(0, rowId.Length - 2));
            if (row != null && !row.IsGame) row = null;
            slot = 1;
        }

        if (row == null || !row.Editable)
        {
            _lastStatus = row == null ? "" : row.IsGame ? GameKeyRemap.NotEditableNow(row.Claim) : row.Claim.NotEditableReason;
            return false;
        }

        if (row.IsGame && GameKeyRemap.NotEditableNow(row.Claim) is { Length: > 0 } notNow)
        {
            _lastStatus = notNow;
            return false;
        }

        if (row.IsGame && slot == 1 && !row.SlotClaim(1).GameSlotExists)
        {
            _lastStatus = GameKeyRemap.NoSlotReason;
            return false;
        }

        _editSlot = row.IsGame && slot == 1 ? 1 : !row.IsGame && slot == GamepadSlot && row.HasGamepadCap ? GamepadSlot : 0;
        _editing = row.Id;
        _pending = null;
        _swapWith = null;
        _takeFrom.Clear();
        _editStatus = EditingGamepad
            ? KeybindsTabLayout.CaptureHelpGamepad + (Keybinds.GamepadConnected ? "" : " " + Keybinds.NoGamepadReason)
            : KeybindsTabLayout.CaptureHelp;
        StartCapture();
        SyncEditBarRow();
        SyncCurrent();
        SyncRow(row.Id);
        return true;
    }

    /// <summary>Wait for the next chord for the row being edited (a new token: an older capture's answer is ignored).</summary>
    private void StartCapture()
    {
        _capturing = true;
        int token = ++_captureToken;
        try
        {
            Keybinds.BeginCapture(result =>
            {
                if (token != _captureToken) return;
                OnCaptured(result);
            }, gamepad: EditingGamepad);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybinds capture threw: " + e.Message);
            _capturing = false;
        }
    }

    /// <summary>
    /// A chord arrived — from the capture, or from the bridge (<c>mods_ui keys op=capture</c>). Nothing
    /// is stored: the edit bar shows what it would clash with, and Save stores it.
    /// </summary>
    internal void OnCaptured(KeyCaptureResult result)
    {
        _capturing = false;
        string editing = _editing;
        var row = _model?.FindRow(editing);
        if (row == null)
        {
            CancelEditState();
            return;
        }

        _swapWith = null;
        _takeFrom.Clear();
        switch (result.Outcome)
        {
            case RebindOutcome.Cancelled:
                CancelEditState();
                SyncRow(editing);
                return;
            case RebindOutcome.Refused:
                // KEYS2: the capture keeps waiting after a refused key (reserved, a Windows-key shortcut);
                // after an AltGr refusal, which ends Keybinds' own wait, it is started again.
                _pending = null;
                _editStatus = KeybindsTabLayout.StillWaiting(result.Reason ?? "That key cannot be used.");
                if (result.StillCapturing) _capturing = true;
                else StartCapture();
                break;
            default:
                if (!result.Chord.IsBound)
                {
                    // KEYS4: a capture that delivers no key (modifiers only) is refused, never an unbind;
                    // unbinding has its own button.
                    _pending = null;
                    _editStatus = KeybindsTabLayout.StillWaiting(KeybindsTabLayout.ModifiersOnlyReason);
                    StartCapture();
                    break;
                }

                if (EditingGamepad)
                {
                    // PAD1: a controller key is refused when something it would meet has it (the game's controller
                    // binding, another bind's controller key); it is not taken from them.
                    string? padRefusal = !result.Chord.IsGamepad ? Keybinds.NotGamepadReason
                        : result.Chord == row.GamepadChord ? null
                        : Keybinds.GamepadClashReason(row.Claim.OwnerId, row.Claim.ActionId, row.Claim.Scope, result.Chord);
                    if (padRefusal != null)
                    {
                        _pending = null;
                        _editStatus = KeybindsTabLayout.StillWaiting(padRefusal);
                        StartCapture();
                        break;
                    }

                    _pending = result.Chord;
                    _editStatus = KeybindsTabLayout.GamepadPreviewSentence(row.GamepadChord, result.Chord);
                    break;
                }

                var claim = row.SlotClaim(_editSlot);
                // KEYS9: a game slot takes one key or mouse button; a chord with a modifier is refused first.
                if (row.IsGame && GameKeyRemap.Refusal(result.Chord) is { } single)
                {
                    _pending = null;
                    _editStatus = KeybindsTabLayout.StillWaiting(single);
                    StartCapture();
                    break;
                }

                // HOLDMOD1: Ctrl, Shift or Alt on its own is a key only for a Progenitor bind that acts while held.
                if (claim.Source == KeybindSource.Progenitor
                    && Keybinds.LoneModifierRefusalFor(claim.OwnerId, claim.ActionId, result.Chord) is { } lone)
                {
                    _pending = null;
                    _editStatus = KeybindsTabLayout.StillWaiting(lone);
                    StartCapture();
                    break;
                }

                // HOLDMOD1 review: an ordinary mod's config-file key may act on a press; it never takes a lone modifier.
                if (claim.Source == KeybindSource.Cfg && result.Chord.IsLoneModifier && claim.DefaultChord != result.Chord)
                {
                    _pending = null;
                    _editStatus = KeybindsTabLayout.StillWaiting(KeybindsTabLayout.CfgLoneModifierReason(result.Chord));
                    StartCapture();
                    break;
                }

                // Requested 2026-10-10: a key something else has is taken, not refused. Save unbinds the holders
                // (named here, before Save, and in their rows after it), so a remap still never leaves a conflict.
                // Refused: a reserved chord, a bare click and a holder this tab can't change.
                var refusal = KeybindRemapGuard.TakeFrom(claim, result.Chord, _takeFrom, out var alsoGame);
                if (refusal != null)
                {
                    _pending = null;
                    _swapWith = SwapCandidate(claim, refusal.Holder);
                    _editStatus = KeybindsTabLayout.StillWaiting(refusal.Reason);
                    StartCapture();
                    break;
                }

                _pending = result.Chord;
                // KEYS2: Swap stays offered beside Save when one Progenitor bind has the key.
                _swapWith = _takeFrom.Count == 1 ? SwapCandidate(claim, _takeFrom[0]) : null;
                _editStatus = _takeFrom.Count > 0
                    ? KeybindsTabLayout.TakeSentence(result.Chord, claim.DisplayName, TakenPhrases(result.Chord))
                    : row.IsGame
                        ? GameKeyRemap.PreviewSentence(claim, result.Chord, alsoGame)
                        : KeybindsTabLayout.PreviewSentence(claim, result.Chord, Preview(claim, result.Chord));
                // KEYS2 review: at the title screen, with no remembered read of the game's keys, say so.
                if (!(claim.Comparable && claim.Chord == result.Chord) && result.Chord.IsBound)
                {
                    string note = KeybindRemapGuard.GameCheckNote();
                    if (note.Length > 0) _editStatus += " " + note;
                }

                break;
        }

        SyncEditBar();
        SyncRow(editing);
    }

    /// <summary>
    /// KEYS2: why <paramref name="claim"/> may not take <paramref name="chord"/>: reserved by Windows or
    /// Steam, or already used by the game, another Progenitor bind or a mod's key setting. Null when free or
    /// when it is the key the row already has.
    /// </summary>
    internal static RemapRefusal? RefusalFor(KeybindClaim claim, KeyChord chord, bool reset)
    {
        if (claim == null || !chord.IsBound) return null;
        if (claim.Comparable && claim.Chord == chord) return null;
        try
        {
            return KeybindRemapGuard.Check(claim.Source, claim.OwnerId, claim.ActionId,
                claim.Comparable ? claim.Chord : KeyChord.None, chord, reset, scope: claim.Scope);
        }
        catch (Exception e)
        {
            // Fail closed: a check that cannot run refuses the key.
            Plugin.Logger?.LogWarning("Ui: keybinds remap check threw: " + e.Message + "; the key is refused.");
            return new RemapRefusal(KeybindRemapGuard.CheckFailedReason, null, ReservedBy.None);
        }
    }

    /// <summary>
    /// KEYS9 (found live 2026-10-10: Move Up could not go back from I to W, because W is also the game's camera-mode
    /// key): a game key is refused only for a reserved chord, a bare click, or a key a mod holds. Another game action on
    /// the same key is not a refusal: the game ships such overlaps itself (Space is Travel Skill, Drop Item and Respawn)
    /// and its own Controls page lets the player accept a collision. <see cref="GameAlsoUsing"/> words it as a warning.
    /// </summary>
    internal static RemapRefusal? GameRefusalFor(KeybindClaim claim, KeyChord chord)
    {
        if (claim == null || !chord.IsBound) return null;
        if (claim.Comparable && claim.Chord == chord) return null;
        try
        {
            var refusal = KeybindRemapGuard.Check(claim.Source, claim.OwnerId, claim.ActionId,
                claim.Comparable ? claim.Chord : KeyChord.None, chord, reset: false, scope: claim.Scope, ignoreGame: true);
            if (refusal != null) return refusal;
            // KEYS9 review: one key on two parts of one movement axis (Move Up and Move Down on I) cancels to no
            // movement, so that overlap is refused, on either slot; other game overlaps only warn.
            foreach (var v in KeybindRemapGuard.PictureSource().Verdicts)
            {
                var c = v.Claim;
                if (c.Comparable && c.Chord == chord && GameKeyRemap.SameAxis(claim, c))
                    return new RemapRefusal(GameKeyRemap.SameAxisReason(chord, c), c, ReservedBy.None);
            }

            return null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybinds remap check threw: " + e.Message + "; the key is refused.");
            return new RemapRefusal(KeybindRemapGuard.CheckFailedReason, null, ReservedBy.None);
        }
    }

    /// <summary>KEYS9: another game action already on <paramref name="chord"/> (the warning in the preview), or null.</summary>
    internal static KeybindClaim? GameAlsoUsing(KeybindClaim claim, KeyChord chord)
    {
        if (claim == null || !chord.IsBound || (claim.Comparable && claim.Chord == chord)) return null;
        try
        {
            var holder = KeybindRemapGuard.HolderOf(claim.Source, claim.OwnerId, claim.ActionId, chord, null, out _, claim.Scope);
            return holder is { Source: KeybindSource.Game } && !(holder.ActionId == claim.ActionId && holder.Slot == claim.Slot) ? holder : null;
        }
        catch { return null; }
    }

    /// <summary>The other Progenitor bind that has the key, when the two could exchange keys cleanly.</summary>
    private static KeybindClaim? SwapCandidate(KeybindClaim claim, KeybindClaim? holder)
    {
        if (holder == null || claim.Source != KeybindSource.Progenitor || holder.Source != KeybindSource.Progenitor) return null;
        try
        {
            return Keybinds.Swap(claim.OwnerId, claim.ActionId, holder.OwnerId, holder.ActionId, dryRun: true, out _) ? holder : null;
        }
        catch { return null; }
    }

    /// <summary>KEYS2: the other bind the edit bar offers to swap keys with, for readback and tests.</summary>
    internal KeybindClaim? SwapWith => _swapWith;

    /// <summary>
    /// KEYS2: <b>Swap</b> on the edit bar. The row being edited takes the refused key and the bind that had
    /// it takes this row's key, after one confirm (the framework dialog; a host without dialogs applies it).
    /// </summary>
    internal void PressSwap()
    {
        var row = _model?.FindRow(_editing);
        var other = _swapWith;
        if (row == null || other == null) return;
        var factory = DialogFactory;
        if (factory == null)
        {
            ConfirmSwap();
            return;
        }

        CloseSwapDialog();
        string mine = SectionTitleOf(row) + " (" + row.Name + ")";
        string theirs = KeybindArbiter.DisplayNameOf(other.OwnerId) + " (" + other.DisplayName + ")";
        var dialog = UiDialog.Open(
            factory, SwapDialogTag, "swap", "keybinds", KeybindsTabLayout.SwapTitle,
            KeybindRemapGuard.SwapSentence(mine, other.Chord, theirs, row.Claim.Chord),
            KeybindsTabLayout.SwapYes, LabelTone.Orange, ModPageCopy.PromptNo);
        var panel = _panel;
        string editing = _editing;
        dialog.OnYes(() =>
        {
            if (!ReferenceEquals(panel, _panel) || !string.Equals(editing, _editing, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger?.LogWarning("Ui: keybinds swap refused: the tab changed.");
                return;
            }

            ConfirmSwap();
        });
    }

    /// <summary>Yes on the swap dialog: both binds exchange keys, both saved, then one rebuild.</summary>
    internal bool ConfirmSwap()
    {
        var row = _model?.FindRow(_editing);
        var other = _swapWith;
        if (row == null || other == null) return false;
        bool ok = Keybinds.Swap(row.Claim.OwnerId, row.Claim.ActionId, other.OwnerId, other.ActionId, dryRun: false, out string? why);
        _lastStatus = ok ? "Swapped." : why ?? "The keys were not swapped.";
        Plugin.Logger?.LogInfo("Ui: keybinds swap " + row.Claim.OwnerId + " " + row.Claim.ActionId + " <-> " + other.OwnerId + " " + other.ActionId + ": " + _lastStatus);
        _captureToken++;
        if (_capturing)
        {
            try { Keybinds.CancelCapture(); }
            catch { /* */ }
        }

        _editing = "";
        _pending = null;
        _swapWith = null;
        _takeFrom.Clear();
        _capturing = false;
        _editStatus = "";
        RequestRebuild();
        return ok;
    }

    internal static void CloseSwapDialog()
    {
        var open = UiDialog.Current;
        if (open == null || !string.Equals(open.Tag, SwapDialogTag, StringComparison.Ordinal)) return;
        try { open.Close(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds swap dialog close threw: " + e.Message); }
    }

    /// <summary>The bridge's capture: as if <paramref name="chord"/> was pressed while <paramref name="rowId"/> waits.</summary>
    internal bool CaptureForBridge(string rowId, KeyChord chord)
    {
        // PAD1: a controller chord edits the row's controller key.
        int slot = chord.IsGamepad ? GamepadSlot : -1;
        bool sameEdit = string.Equals(_editing, rowId, StringComparison.OrdinalIgnoreCase) && EditingGamepad == chord.IsGamepad;
        if (!sameEdit && !BeginEdit(rowId, slot)) return false;
        _captureToken++;
        try { Keybinds.CancelCapture(); }
        catch { /* */ }
        OnCaptured(new KeyCaptureResult(RebindOutcome.Bound, chord, null));
        return true;
    }

    /// <summary>What the winner rule would say about <paramref name="claim"/> on <paramref name="chord"/>.</summary>
    internal static KeybindVerdict? Preview(KeybindClaim claim, KeyChord chord)
    {
        // KEYS9: a game key that passed the guard clashes with nothing; it is never one of the mod lists.
        if (claim == null || claim.Source == KeybindSource.Game) return null;
        try
        {
            var game = KeybindArbiter.GameSnapshot();
            var progenitor = new List<KeybindClaim>(KeybindArbiter.ReadProgenitor());
            var cfg = new List<KeybindClaim>(KeybindArbiter.ReadCfg());
            var list = claim.Source == KeybindSource.Cfg ? cfg : progenitor;
            list.RemoveAll(c => string.Equals(c.Key, claim.Key, StringComparison.OrdinalIgnoreCase));
            var moved = new KeybindClaim(claim.Source, claim.OwnerId, claim.ActionId, claim.DisplayName, chord,
                help: claim.Help, defaultChord: claim.DefaultChord, defaultText: claim.DefaultText, editable: claim.Editable,
                notEditableReason: claim.NotEditableReason, entry: claim.Entry, keyCodeOnly: claim.KeyCodeOnly, scope: claim.Scope,
                panel: claim.Panel);
            list.Add(moved);
            var picture = KeybindArbiter.Compute(game.Available ? game.Claims : Array.Empty<KeybindClaim>(), progenitor, cfg);
            return picture.Find(moved.Key);
        }
        catch { return null; }
    }

    internal void SaveEdit()
    {
        var row = _model?.FindRow(_editing);
        if (row == null || _pending is not { } chord) return;
        Store(row, chord, take: _takeFrom.ToArray());
    }

    /// <summary>What Save takes the pending key from, for readback and tests.</summary>
    internal IReadOnlyList<KeybindClaim> TakeFromClaims => _takeFrom;

    /// <summary>The row a claim is shown on: a game slot's action row (its secondary key has no row of its own).</summary>
    private static string RowIdOf(KeybindClaim claim) =>
        claim.Source == KeybindSource.Game ? KeybindRemapGuard.KeyOf(claim.Source, claim.OwnerId, claim.ActionId) : claim.Key;

    /// <summary>The key a holder keeps once <paramref name="chord"/> is taken (a game action's other slot), or empty.</summary>
    private string KeptKey(KeybindClaim holder, KeyChord chord)
    {
        if (holder.Source != KeybindSource.Game) return "";
        var row = _model?.FindRow(RowIdOf(holder));
        if (row == null) return "";
        var other = row.SlotClaim(holder.Slot == 1 ? 0 : 1);
        if (string.Equals(other.Key, holder.Key, StringComparison.OrdinalIgnoreCase)) return "";
        if (other.Comparable) return other.Chord.IsBound && other.Chord != chord ? other.Chord.DisplayText : "";
        string text = other.ChordText ?? "";
        return text.Length == 0 || string.Equals(text, "None", StringComparison.OrdinalIgnoreCase) ? "" : text;
    }

    private List<(KeybindClaim Holder, string Kept)> TakenPhrases(KeyChord chord)
    {
        var list = new List<(KeybindClaim, string)>(_takeFrom.Count);
        foreach (var h in _takeFrom) list.Add((h, KeptKey(h, chord)));
        return list;
    }

    /// <summary>Unbind a holder's key (the first half of a take), through its own path.</summary>
    private static bool ReleaseKey(KeybindClaim holder, out string status)
    {
        status = "";
        try
        {
            switch (holder.Source)
            {
                case KeybindSource.Game:
                    var g = GameKeyRemap.Store(holder, holder.Slot, KeyChord.None, reset: false);
                    status = g.Status;
                    return g.Ok;
                case KeybindSource.Progenitor:
                    bool ok = Keybinds.TryRebind(holder.OwnerId, holder.ActionId, KeyChord.None, Keybinds.RebindCheck.Full, out string? why);
                    status = why ?? "";
                    return ok;
                case KeybindSource.Cfg:
                    var r = SettingsCatalog.WriteKey(holder, KeyChord.None);
                    status = r.Error ?? "";
                    return r.State == SettingWriteState.Accepted;
            }
        }
        catch (Exception e) { status = e.Message; }

        return false;
    }

    /// <summary>Put a released holder's key back when the take could not finish.</summary>
    private static void RestoreKey(KeybindClaim holder)
    {
        try
        {
            switch (holder.Source)
            {
                case KeybindSource.Game:
                    GameKeyRemap.Store(holder, holder.Slot, holder.Chord, reset: false);
                    break;
                case KeybindSource.Progenitor:
                    Keybinds.TryRebind(holder.OwnerId, holder.ActionId, holder.Chord, Keybinds.RebindCheck.ReservedOnly, out _);
                    break;
                case KeybindSource.Cfg:
                    SettingsCatalog.WriteKey(holder, holder.Chord);
                    break;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds could not give " + holder.Key + " its key back: " + e.Message); }
    }

    internal void UnbindEdit()
    {
        var row = _model?.FindRow(_editing);
        if (row == null) return;
        Store(row, KeyChord.None);
    }

    internal void CancelEdit()
    {
        string editing = _editing;
        _captureToken++;
        if (_capturing)
        {
            try { Keybinds.CancelCapture(); }
            catch { /* */ }
        }

        CancelEditState();
        SyncRow(editing);
    }

    private void CancelEditState()
    {
        _editing = "";
        _editSlot = 0;
        _pending = null;
        _swapWith = null;
        _takeFrom.Clear();
        _editStatus = "";
        _capturing = false;
        SyncEditBarRow();
        SyncCurrent();
    }

    /// <summary>
    /// The reset arrow. During a change to this row (user report 2026-10-10: it stayed dimmed until Save) it drops
    /// the change first, then puts the default back when the stored key differs from it.
    /// </summary>
    internal void ResetRow(string rowId)
    {
        var row = _model?.FindRow(rowId);
        if (row == null) return;
        if (string.Equals(_editing, row.Id, StringComparison.OrdinalIgnoreCase))
        {
            CancelEdit();
            _lastStatus = row.CanReset ? "" : "Change cancelled; the key stays at its default.";
            SyncCurrent();
        }

        if (!row.CanReset) return;
        if (row.IsGame)
        {
            // KEYS9: both slots back to the game's own default, as the game's per-key restore does.
            _editSlot = 0;
            Store(row, KeyChord.None, reset: true);
            return;
        }

        // PAD1: the controller key goes back to its default too.
        if (row.GamepadChanged && row.Gamepad!.Claim.DefaultChord is { } padDefault)
        {
            _editSlot = GamepadSlot;
            Store(row, padDefault, reset: true);
            row = _model?.FindRow(rowId) ?? row;
        }

        if (!row.Claim.Modified || row.Claim.DefaultChord is not { } d) return;
        _editSlot = 0;
        Store(row, d, reset: true);
    }

    internal void ClearRow(string rowId)
    {
        var row = _model?.FindRow(rowId);
        if (row == null || !row.CanClear) return;
        // KEYS9: on a game row the gutter's Unbind clears the primary key, as the game's own Clear does.
        _editSlot = 0;
        Store(row, KeyChord.None);
    }

    /// <summary>
    /// Write one key through its own path, then rebuild so every row reads the new picture. <paramref name="take"/>:
    /// the holders unbound first (<see cref="KeybindRemapGuard.TakeFrom"/>); if any step fails, the ones already
    /// unbound get their keys back. Each holder's row then says where its key went, and this row what it took.
    /// </summary>
    internal bool Store(KeybindsRowModel row, KeyChord chord, bool reset = false, IReadOnlyList<KeybindClaim>? take = null)
    {
        int slot = row.IsGame || (EditingGamepad && row.HasGamepadCap) ? _editSlot : 0;
        var released = new List<(KeybindClaim Holder, string Kept)>();
        string status = "";
        bool ok = true;
        if (take != null)
        {
            foreach (var holder in take)
            {
                string kept = KeptKey(holder, chord);
                if (!ReleaseKey(holder, out string why))
                {
                    status = KeybindsTabLayout.TakeFailed(chord, holder, why);
                    ok = false;
                    break;
                }

                released.Add((holder, kept));
            }
        }

        if (ok) ok = StoreOne(row, slot, chord, reset, out status);
        if (!ok)
        {
            for (int i = released.Count - 1; i >= 0; i--) RestoreKey(released[i].Holder);
        }
        else if (released.Count > 0)
        {
            _notes[row.Id] = KeybindsTabLayout.TookNote(chord, released);
            foreach (var (holder, kept) in released)
                _notes[RowIdOf(holder)] = KeybindsTabLayout.LostNote(chord, row.Name, kept);
            status = (status.Length > 0 ? status + " " : "") + _notes[row.Id];
        }
        else
        {
            _notes.Remove(row.Id);
        }

        _lastStatus = status;
        _captureToken++;
        if (_capturing)
        {
            try { Keybinds.CancelCapture(); }
            catch { /* */ }
        }

        _editing = "";
        _editSlot = 0;
        _pending = null;
        _swapWith = null;
        _takeFrom.Clear();
        _capturing = false;
        _editStatus = "";
        foreach (var (holder, _) in released)
            Plugin.Logger?.LogInfo("Ui: keybinds took " + chord + " from " + holder.Key + (ok ? "" : " (given back)"));
        Plugin.Logger?.LogInfo("Ui: keybinds " + row.Claim.OwnerId + " " + row.Claim.ActionId + (row.IsGame ? "#" + (slot + 1) : "")
            + " -> " + (reset && row.IsGame ? "default" : chord.ToString()) + ": " + (ok ? "saved" : "refused") + (status.Length > 0 ? " (" + status + ")" : ""));
        RequestRebuild();
        return ok;
    }

    private static bool StoreOne(KeybindsRowModel row, int slot, KeyChord chord, bool reset, out string status)
    {
        status = "";
        if (row.IsGame)
        {
            // KEYS9: the game's own key. The same KEYS2 guard first (a reset puts the game's default back,
            // which is not checked: the game ships it), then the game's input settings.
            var claim = row.SlotClaim(slot);
            if (!reset)
            {
                if (GameKeyRemap.Refusal(chord) is { } single)
                {
                    status = single;
                    return false;
                }

                if (GameRefusalFor(claim, chord) is { } clash)
                {
                    status = clash.Reason;
                    return false;
                }
            }

            if (reset)
            {
                var first = GameKeyRemap.Store(row.Claim, 0, KeyChord.None, reset: true);
                // KEYS9 review: an action with one keyboard binding has no second slot to reset.
                var second = row.Secondary is { Claim.GameSlotExists: true }
                    ? GameKeyRemap.Store(row.Claim, 1, KeyChord.None, reset: true) : first;
                status = !first.Ok ? first.Status : second.Status;
                return first.Ok && second.Ok;
            }

            var written = GameKeyRemap.Store(row.Claim, slot, chord, reset: false);
            status = written.Status;
            return written.Ok;
        }

        if (row.Claim.Source == KeybindSource.Progenitor && slot == GamepadSlot)
        {
            // PAD1: the controller key, through its own entry and check.
            bool padOk = Keybinds.TryRebindGamepad(row.Claim.OwnerId, row.Claim.ActionId, chord, reset, out string? padWhy);
            status = padOk ? "Saved." : padWhy ?? Keybinds.NotRegisteredReason;
            return padOk;
        }

        if (row.Claim.Source == KeybindSource.Progenitor)
        {
            // KEYS2: Keybinds refuses a reserved chord and any chord something else has (a reset's default
            // included), with the row-sized reason.
            bool ok = Keybinds.TryRebind(row.Claim.OwnerId, row.Claim.ActionId, chord,
                reset ? Keybinds.RebindCheck.Reset : Keybinds.RebindCheck.Full, out string? why);
            status = ok ? "Saved." : why ?? Keybinds.NotRegisteredReason;
            return ok;
        }

        if (row.Claim.Source == KeybindSource.Cfg)
        {
            // KEYS2: the same guard for an ordinary mod's key setting before anything is written.
            if (RefusalFor(row.Claim, chord, reset) is { } refusal)
            {
                status = refusal.Reason;
                return false;
            }

            var result = SettingsCatalog.WriteKey(row.Claim, chord);
            bool ok = result.State == SettingWriteState.Accepted;
            status = ok ? SettingsCatalog.KeySavedNote : result.Error;
            return ok;
        }

        status = GameKeyRemap.OnlyInGameMenuReason;
        return false;
    }

    private void RequestRebuild()
    {
        var rebuild = Rebuild;
        if (rebuild != null)
        {
            try { rebuild(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds rebuild threw: " + e.Message); }
            return;
        }

        if (_panel != null) SyncAll();
    }

    /// <summary>
    /// KEYS4 (found live 2026-09-30: the tab kept a hot-reloaded mod's old key until it was reopened):
    /// recompute the picture and rewrite the rows in place when they are still the same rows in the same
    /// order, so the scroll, the selection and a capture in progress all stay. False when a row came or
    /// went (the caller rebuilds, keeping the scroll).
    /// </summary>
    internal bool RefreshInPlace()
    {
        if (_panel == null) return false;
        var model = BuildModel();
        var ids = new List<string>();
        foreach (var s in model.Sections)
            foreach (var r in s.Rows) ids.Add(r.Id);
        if (ids.Count != _rows.Count) return false;
        // Keybind groups: a group defined, removed or retitled changes the headings, not only the rows.
        if (_model == null || model.Sections.Count != _model.Sections.Count) return false;
        for (int i = 0; i < model.Sections.Count; i++)
            if (!string.Equals(model.Sections[i].Id, _model.Sections[i].Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(model.Sections[i].Title, _model.Sections[i].Title, StringComparison.Ordinal))
                return false;
        for (int i = 0; i < ids.Count; i++)
            if (!string.Equals(ids[i], _rows[i].Model.Id, StringComparison.OrdinalIgnoreCase)) return false;
        _model = model;
        foreach (var r in _rows) SyncRow(r.Model.Id);
        SyncEditBar();
        return true;
    }

    /// <summary>Recompute the picture and rewrite the rows in place (a host that cannot rebuild).</summary>
    private void SyncAll()
    {
        _model = BuildModel();
        foreach (var r in _rows) SyncRow(r.Model.Id);
        SyncEditBarRow();
        SyncCurrent();
    }

    private void SyncRow(string rowId)
    {
        RowBinding? binding = null;
        foreach (var r in _rows)
            if (string.Equals(r.Model.Id, rowId, StringComparison.OrdinalIgnoreCase)) { binding = r; break; }
        if (binding == null || binding.Name.IsDisposed) return;
        var model = _model?.FindRow(rowId) ?? binding.Model;
        bool editing = string.Equals(_editing, rowId, StringComparison.OrdinalIgnoreCase);
        string keyText = editing && _capturing ? KeybindsTabLayout.CapturingText
            : editing && _pending is { } p ? (p.IsBound ? p.DisplayText : "None")
            : model.PrimaryText;
        if (string.Equals(keyText, "None", StringComparison.OrdinalIgnoreCase) && !editing) keyText = "";
        // KEYS3 live follow-up: caps draw the compact chord, shrunk to fit before any ellipsis.
        string primary = string.Equals(keyText, KeybindsTabLayout.CapturingText, StringComparison.Ordinal)
            ? keyText : KeybindsTabLayout.CapCaption(keyText);
        string secondary = model.SecondaryCaption;
        if (editing && ((model.IsGame && _editSlot == 1) || (model.HasGamepadCap && EditingGamepad)))
        {
            // KEYS9: the second slot is the one waiting; the primary cap keeps its key. PAD1: so is the controller key.
            secondary = _capturing ? KeybindsTabLayout.CapturingText
                : _pending is { } p2 ? KeybindsTabLayout.CapCaption(p2.IsBound ? p2.DisplayText : "") : secondary;
            primary = model.PrimaryCaption;
        }

        SetCapSize(binding.Name, CellKey, primary);
        SetCapSize(binding.Name, CellKey2, secondary);
        binding.Name.SetCell(CellKey, text: primary, tone: model.PrimaryTone);
        binding.Name.SetCell(CellKey2, text: secondary, tone: model.SecondaryTone);
        string marker = KeybindsTabLayout.RowMarker(model);
        binding.Name.SetCell(KeybindsTabLayout.CellMarker, text: marker, visible: marker.Length > 0);
        if (model.HasGutter)
        {
            // KEYS3: the gutter stays drawn, like the game's reset arrow; an action that does not apply
            // is disabled rather than hidden, so every row's caps keep the same x.
            // User report 2026-10-10: during a change the arrow is live too (it drops the change; see ResetRow).
            binding.Name.SetCell(CellReset, tone: model.CanReset || editing ? CellTone.Normal : CellTone.Disabled);
            binding.Name.SetCell(CellClear, tone: model.CanClear ? CellTone.Normal : CellTone.Disabled);
        }
    }

    private static void SetCapSize(IUiCellRow row, string key, string caption)
    {
        foreach (var cell in row.Cells)
            if (string.Equals(cell.Key, key, StringComparison.Ordinal)) cell.FontSizeOverride = KeybindsTabLayout.CapFontSize(caption);
    }

    private void SyncEditBar()
    {
        SyncEditBarRow();
        SyncDescription();
    }

    private void SyncEditBarRow()
    {
        var bar = _edit;
        if (bar == null || bar.IsDisposed) return;
        var row = _model?.FindRow(_editing);
        bool on = row != null;
        SetVisible(bar, on);
        if (!on) return;
        string title = KeybindsTabLayout.EditTitle(SectionTitleOf(row!), row!.Name
            + (row.IsGame && _editSlot == 1 ? KeybindsTabLayout.SecondKeySuffix : EditingGamepad ? KeybindsTabLayout.ControllerKeySuffix : ""));
        // KEYS3 polish (user report 2026-09-30): the bar stays short; the full status (a refusal, the
        // preview) is in the description column and the edit readback.
        bar.SetCell(CellEditText, text: KeybindsTabLayout.EditBarText(title, _capturing, _pending),
            tone: !EditingGamepad && _pending is { } p && (_takeFrom.Count > 0 || Preview(row.SlotClaim(_editSlot), p) is { InConflict: true }) ? CellTone.Warn : CellTone.Normal);
        bar.SetCell(CellSave, visible: _pending != null);
        bar.SetCell(CellSwap, visible: _swapWith != null);
        bar.SetCell(CellUnbind, visible: EditingGamepad ? row.GamepadChord.IsBound : row.IsGame ? !IsUnbound(row.SlotClaim(_editSlot)) : row.CanClear);
    }

    private static bool IsUnbound(KeybindClaim claim) =>
        !claim.Chord.IsBound && string.Equals(claim.ChordText, "None", StringComparison.OrdinalIgnoreCase);

    private string SectionTitleOf(KeybindsRowModel row)
    {
        var s = _model?.Find(row.SectionId);
        return s?.Title ?? row.Claim.OwnerId;
    }

    // ---- reset all --------------------------------------------------------------------------

    internal void PressResetAll()
    {
        var rows = _model?.ResettableRows() ?? new List<KeybindsRowModel>();
        if (rows.Count == 0)
        {
            Plugin.Logger?.LogInfo("Ui: keybinds reset-all: no mod key differs from its default.");
            return;
        }

        var factory = DialogFactory;
        if (factory == null)
        {
            ConfirmResetAll();
            return;
        }

        CloseResetAllDialog();
        var dialog = UiDialog.Open(
            factory, ResetAllDialogTag, "reset-all", "keybinds",
            "Reset all mod keys?",
            rows.Count.ToString(CultureInfo.InvariantCulture) + (rows.Count == 1 ? " mod key goes" : " mod keys go")
            + " back to the key its mod ships with. The game's keys are not changed.",
            "Yes, reset", LabelTone.Orange, ModPageCopy.PromptNo,
            // Every remapped mod key is wiped back to its default: no undo, so Enter does not answer it.
            danger: true);
        var panel = _panel;
        dialog.OnYes(() =>
        {
            if (!ReferenceEquals(panel, _panel))
            {
                Plugin.Logger?.LogWarning("Ui: keybinds reset-all refused: the tab was rebuilt.");
                return;
            }

            ConfirmResetAll();
        });
    }

    /// <summary>Yes on the dialog: every editable, changed mod key back to its default, then one rebuild.</summary>
    internal int ConfirmResetAll()
    {
        var rows = _model?.ResettableRows() ?? new List<KeybindsRowModel>();
        int done = 0;
        foreach (var row in rows)
        {
            if (row.Claim.DefaultChord is not { } d) continue;
            if (StoreOne(row, 0, d, reset: true, out string status)) done++;
            else Plugin.Logger?.LogWarning("Ui: keybinds reset of " + row.Id + " refused: " + status);
        }

        _lastStatus = done + " of " + rows.Count + " reset.";
        Plugin.Logger?.LogInfo("Ui: keybinds reset-all: " + _lastStatus);
        RequestRebuild();
        return done;
    }

    internal static void CloseResetAllDialog()
    {
        var open = UiDialog.Current;
        if (open == null || !string.Equals(open.Tag, ResetAllDialogTag, StringComparison.Ordinal)) return;
        try { open.Close(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds reset-all dialog close threw: " + e.Message); }
    }

    // ---- readback ---------------------------------------------------------------------------

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

    private object? ToolbarReadback()
    {
        var m = _model;
        int shown = 0;
        foreach (var r in _rows) if (r.Visible) shown++;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["query"] = Query,
            ["filter"] = Filter.ToString().ToLowerInvariant(),
            ["filters"] = new Dictionary<string, object?>
            {
                ["all"] = _rows.Count,
                ["conflicts"] = m?.ConflictCount ?? 0,
                ["changed"] = m?.ChangedCount ?? 0,
            },
            ["shown"] = shown,
            ["total"] = _rows.Count,
            ["resettable"] = m?.ResettableRows().Count ?? 0,
            ["gameAvailable"] = m?.GameAvailable ?? false,
            ["jumpTargets"] = JumpTargetsReadback(),
            ["currentJump"] = _currentJump,
            ["groups"] = ToolbarGroupsReadback(),
            ["lastJump"] = LastJump?.Readback(),
        };
    }

    /// <summary>The toolbar's groups as drawn: the filter's three segments, the section jumps, the picker.</summary>
    private Dictionary<string, object?> ToolbarGroupsReadback() => new(StringComparer.Ordinal)
    {
        ["filter"] = new List<object?> { CellAll, CellConflicts, CellChanged },
        ["jumps"] = new List<object?> { CellModJump, CellGameJump },
        ["picker"] = CellJumpToMod,
    };

    private object? EditReadback() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["editing"] = _editing,
        ["slot"] = _editSlot,
        ["capturing"] = _capturing,
        ["pending"] = _pending is { } p ? (p.IsBound ? p.DisplayText : "None") : null,
        ["takeFrom"] = _takeFrom.ConvertAll(c => c.Key),
        ["status"] = _editStatus,
        ["lastStatus"] = _lastStatus,
        ["swapWith"] = _swapWith == null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner"] = _swapWith.OwnerId,
            ["action"] = _swapWith.ActionId,
            ["chord"] = _swapWith.ChordText,
            ["gets"] = _model?.FindRow(_editing)?.Claim.ChordText ?? "",
        },
        ["visible"] = _edit is { IsDisposed: false } e && e.Visible,
    };

    private Dictionary<string, object?> SectionReadback(KeybindsSectionModel? section, string id, string title)
    {
        string source = "";
        if (section != null)
        {
            if (section.IsGame) source = "game";
            else
            {
                bool cfg = false, prog = false;
                foreach (var r in section.Rows)
                {
                    if (r.Claim.Source == KeybindSource.Cfg) cfg = true;
                    else prog = true;
                }

                source = cfg && prog ? "mixed" : cfg ? "cfg" : "progenitor";
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["title"] = title,
            ["owner"] = section?.OwnerId ?? KeybindArbiter.GameOwnerId,
            ["source"] = source,
            ["count"] = section?.Rows.Count ?? 0,
            ["countText"] = section?.CountText ?? "",
            ["shown"] = section == null ? 0 : KeybindsTabModel.VisibleRows(section, Filter, Query),
            ["hasModPage"] = section?.HasModPage ?? false,
            ["available"] = section != null,
            // KEYS3: a game section is one Bindings Menu category; its title is the game's word when read.
            ["category"] = section?.Category ?? "",
            ["titleSource"] = section is { IsGame: true } ? GameKeybindCategories.Source : "",
            // Keybind groups: the group id, its read-only rows, and the unavailable note (read now; as drawn).
            ["group"] = section?.GroupId ?? "",
            ["infoRows"] = section == null ? 0 : section.Rows.Count - section.KeyRowCount,
            ["unavailable"] = section?.UnavailableReason,
            ["unavailableShown"] = section is { IsGroup: true } ? GroupNoteShown(section) : "",
        };
    }

    private Dictionary<string, object?> RowReadback(RowBinding b)
    {
        var model = _model?.FindRow(b.Model.Id) ?? b.Model;
        var d = model.Readback(b.Visible);
        d["editing"] = string.Equals(_editing, model.Id, StringComparison.OrdinalIgnoreCase);
        d["nameRow"] = b.Name.Key;
        d["helpRow"] = b.Help?.Key ?? "";
        // KEYS3: the highlight follows the hovered (else selected) row.
        d["current"] = string.Equals(CurrentRow, model.Id, StringComparison.OrdinalIgnoreCase);
        d["hovered"] = string.Equals(_hovered, model.Id, StringComparison.OrdinalIgnoreCase);
        d["selected"] = string.Equals(_selected, model.Id, StringComparison.OrdinalIgnoreCase);
        return d;
    }

    /// <summary>The whole tab as one object, for <c>mods_ui status</c> / <c>keys op=read</c> and tests.</summary>
    internal Dictionary<string, object?> Describe()
    {
        var sections = new List<object?>();
        foreach (var (s, _, _) in _sections) sections.Add(SectionReadback(s, s.Id, s.Title));
        var rows = new List<object?>();
        foreach (var r in _rows) rows.Add(RowReadback(r));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["toolbar"] = ToolbarReadback(),
            ["conflictsHeader"] = ConflictsHeaderReadback(),
            ["edit"] = EditReadback(),
            ["gameNote"] = _model is { HasGameSections: false } m ? m.GameNote : GameHeadingNote,
            ["gameFromCache"] = _model?.GameFromCache ?? false,
            ["sections"] = sections,
            ["rows"] = rows,
            // KEYS3: the description column, the borrowed visuals and the category titles' source.
            ["description"] = DescriptionReadback(),
            ["art"] = KeybindArtLadder.Describe(),
            ["resettable"] = _model?.ResettableRows().Count ?? 0,
            // KEYS4: the key-conflict dialog, shown or waiting.
            ["conflictPrompt"] = KeybindConflictPrompt.Describe(),
        };
    }

    /// <summary>Stop a capture this pane started (the pane is closing or rebuilding).</summary>
    internal void Release()
    {
        _captureToken++;
        if (_capturing)
        {
            try { Keybinds.CancelCapture(); }
            catch { /* */ }
        }

        _capturing = false;
        _panel = null;
        _aside = null;
        _descHeading = null;
        _descBody = null;
        _descWarning = null;
        _descInfo = null;
        _descHint = null;
        _footer = null;
        _painted = "";
        _readback.Clear();
        CloseResetAllDialog();
        CloseSwapDialog();
    }
}
