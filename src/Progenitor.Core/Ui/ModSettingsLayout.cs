using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>Where a section of a mod page's settings came from.</summary>
internal enum ModSettingsSectionKind
{
    /// <summary>One <c>.Settings(title, …)</c> group, in registration order.</summary>
    Group = 0,

    /// <summary>Config entries of the mod that no group names.</summary>
    General = 1,

    /// <summary>The mod's keybinds.</summary>
    Keys = 2,

    /// <summary>Registered setting actions (<c>ctx.Menu.RegisterAction</c>).</summary>
    Actions = 3,

    /// <summary>SETTINGS1 stage 2: one <c>Section(...)</c> of the mod's settings page builder (<c>ctx.Settings.Page</c>).</summary>
    Page = 4,
}

/// <summary>SETTINGS1 stage 2: what one item of a section is.</summary>
internal enum ModSettingsItemKind
{
    /// <summary>A setting row (config entry, keybind or registered action).</summary>
    Setting = 0,

    /// <summary>A builder button (optionally confirmed).</summary>
    Button = 1,

    /// <summary>A live read-only line.</summary>
    Info = 2,

    /// <summary>A horizontal rule.</summary>
    Divider = 3,

    /// <summary>
    /// Settings organization: a link row to another place (<see cref="SettingsLinks"/>) — the sentence where a name
    /// goes and an <b>Open</b> key cap. Core only (Progenitor's page's link to the Configuration tab).
    /// </summary>
    Link = 5,

    /// <summary>
    /// Settings organization stage 5: a value of the joined server read and written through the admin channel
    /// (<see cref="AdminSettingSpec"/>). Core only (Progenitor's Server settings).
    /// </summary>
    Admin = 4,
}

/// <summary>
/// SETTINGS1 stage 2: the per-row options a settings page builder attaches (docs/mod-settings.md,
/// "Common options"). A stage 1 row has none. The predicates are the plugin's delegates: they are
/// dropped by <see cref="Release"/> when the page registration goes, so nothing of an unloaded
/// generation is held.
/// </summary>
internal sealed class ModSettingOptions
{
    internal ModSettingOptions Copy() => (ModSettingOptions)MemberwiseClone();
    internal string CompatibilityReason { get; set; } = "";
    internal string? Label { get; set; }

    internal string? Help { get; set; }

    internal Func<bool>? ShowIf { get; set; }

    internal Func<bool>? EnableIf { get; set; }

    /// <summary>
    /// The declarative rules the author gave (<c>ShowWhen</c> / <c>EnableWhen</c>): sent with a published setting's
    /// definition. Locally they are already folded into <see cref="ShowIf"/> / <see cref="EnableIf"/>.
    /// </summary>
    internal SettingCondition? ShowWhen { get; set; }

    internal SettingCondition? EnableWhen { get; set; }

    /// <summary>A published setting placed on the page by id (a <c>PerPlayer</c> setting): its row id.</summary>
    internal string? ScopedRowId { get; set; }

    /// <summary>
    /// A placed published setting the server does not provide now (offline, or a server without it): the item only
    /// claims <see cref="ScopedRowId"/>, so this side's cfg row of the same id is not drawn as a raw field, and draws nothing.
    /// </summary>
    internal bool ClaimOnly { get; set; }

    internal bool NeedsRestart { get; set; }

    internal bool NeedsServerPart { get; set; }

    /// <summary>What the mod does without its server part, shown while the row is grayed for it.</summary>
    internal string Fallback { get; set; } = "";

    /// <summary>Settings scopes: hide the row, instead of graying it, while the half it needs is absent (<c>HideWhenAlone()</c>).</summary>
    internal AloneBehavior WhenAlone { get; set; }

    /// <summary>The scope the mod declared, or null when it declared none.</summary>
    internal SettingScope? Scope { get; set; }

    /// <summary>The control the builder asked for, or null for the automatic choice.</summary>
    internal SettingControl? Control { get; set; }

    /// <summary>Lines of a multi-line text row.</summary>
    internal int Lines { get; set; } = 3;

    /// <summary>The stored value when the page was registered (the mod's load), for needs-restart.</summary>
    internal string? LoadValue { get; set; }

    /// <summary>Set once a predicate has thrown, so it is logged once, not once per evaluation.</summary>
    internal bool ShowIfFaulted { get; set; }

    internal bool EnableIfFaulted { get; set; }

    /// <summary>True when the page registered server edits for this row's entry (scope server).</summary>
    internal bool ServerEditsRegistered { get; set; }

    /// <summary>True when scope server was declared on an entry that is not synced (logged at registration).</summary>
    internal bool ScopeMismatch { get; set; }

    /// <summary>JOINFLOW1: a date-time row's seconds function (true: the picker edits seconds), read when it opens.</summary>
    internal Func<bool>? DateTimeSeconds { get; set; }

    /// <summary>JOINFLOW1: a date-time row's zone function, read when the row is drawn and when the picker opens.</summary>
    internal Func<TimeZoneInfo>? DateTimeZone { get; set; }

    /// <summary>SERVERDESC1: the expanded editor's counter limit, read when it opens (null: the row's own limit).</summary>
    internal Func<int>? CounterLimit { get; set; }

    /// <summary>
    /// Settings organization (Core only): one more <c>widget_read</c> key on a button or info line, read when the row
    /// is read (Progenitor's page's uninstall rows carry <c>progenitorUninstall</c>). Null: none.
    /// </summary>
    /// <summary>
    /// PENDING1 (Core only): the info line is a status row (the muted info-row look of the Local server group, never a
    /// setting's value box), its line in the tone this returns (muted; gold to draw an admin's eye; warn).
    /// </summary>
    internal Func<CellTone>? StatusTone { get; set; }

    /// <summary>
    /// TOKENTIPS2 (Core only): the status row's line wraps (every line kept, the row as tall as the text) instead of one
    /// ellipsized line. Live 2026-10-06: the Placeholders line was cut after its second token.
    /// </summary>
    internal bool StatusWrap { get; set; }

    /// <summary>
    /// RADIODESC1 (Core only): with <see cref="StatusWrap"/>, a fixed height of this many lines for a line whose text
    /// changes while it is shown (the Players see preview); 0 sizes the row to its text once (a fixed text).
    /// </summary>
    internal int StatusWrapLines { get; set; }

    /// <summary>NOPROGMSG1 (Core only): a text field's muted hint while it is empty (the value empty stands for).</summary>
    internal string? Placeholder { get; set; }

    /// <summary>NOPROGMSG1 (Core only): what an empty value reads as on a read-only row (the default it stands for).</summary>
    internal string? EmptyShows { get; set; }

    /// <summary>PENDING1 (Core only): the hover/help text read when the row is described, instead of <see cref="Help"/>.</summary>
    internal Func<string>? HelpNow { get; set; }

    internal string? ExtraReadbackKey { get; set; }

    internal Func<object?>? ExtraReadback { get; set; }

    /// <summary>Settings organization (Core only): a button drawn in the danger tone (the uninstall row).</summary>
    internal bool Destructive { get; set; }

    /// <summary>Three homes (Core only): not counted as a setting of its group (<see cref="SettingsDrawers.Counts"/>).</summary>
    internal bool Uncounted { get; set; }

    internal void Release()
    {
        ShowIf = null;
        EnableIf = null;
        ShowWhen = null;
        EnableWhen = null;
        DateTimeSeconds = null;
        DateTimeZone = null;
        CounterLimit = null;
        ExtraReadback = null;
    }
}

/// <summary>SETTINGS1 stage 2: one item of a section — a setting row, a button, an info line or a divider.</summary>
internal sealed class ModSettingsItem
{
    private ModSettingsItem(ModSettingsItemKind kind, string id)
    {
        Kind = kind;
        Id = id ?? "";
    }

    internal ModSettingsItemKind Kind { get; }

    /// <summary>The row id for a setting, else <c>&lt;plugin&gt;/button/&lt;caption&gt;</c> and the like.</summary>
    internal string Id { get; }

    internal SettingsRow? Row { get; private set; }

    internal ModSettingOptions Options { get; private set; } = new();

    /// <summary>A button's caption or an info line's label.</summary>
    internal string Label { get; private set; } = "";

    internal Action? ButtonAction { get; private set; }

    /// <summary>A button's confirmation question, or null.</summary>
    internal string? Confirm { get; private set; }

    internal Func<string>? InfoValue { get; private set; }

    internal double RefreshSeconds { get; private set; }

    internal static ModSettingsItem ForRow(SettingsRow row, ModSettingOptions? options = null) =>
        new(ModSettingsItemKind.Setting, row?.RowId ?? "") { Row = row, Options = options ?? new ModSettingOptions() };

    internal static ModSettingsItem ForButton(string id, string caption, Action action, string? confirm, ModSettingOptions? options) =>
        new(ModSettingsItemKind.Button, id)
        {
            Label = caption ?? "", ButtonAction = action, Confirm = string.IsNullOrWhiteSpace(confirm) ? null : confirm,
            Options = options ?? new ModSettingOptions(),
        };

    internal static ModSettingsItem ForInfo(string id, string label, Func<string> value, double refreshSeconds, ModSettingOptions? options) =>
        new(ModSettingsItemKind.Info, id)
        {
            Label = label ?? "", InfoValue = value, RefreshSeconds = refreshSeconds, Options = options ?? new ModSettingOptions(),
        };

    internal static ModSettingsItem ForDivider(string id) => new(ModSettingsItemKind.Divider, id);

    /// <summary>Settings organization: a link row (<see cref="SettingsLinks"/>) keyed <paramref name="key"/> to <paramref name="target"/>.</summary>
    internal static ModSettingsItem ForLink(string id, string key, string text, Action open, string target, ModSettingOptions? options) =>
        new(ModSettingsItemKind.Link, id)
        {
            Label = text ?? "", ButtonAction = open, LinkKey = key ?? "", LinkTarget = target ?? "",
            Options = options ?? new ModSettingOptions(),
        };

    /// <summary>Settings organization stage 5: an admin-channel row (<see cref="AdminSettingSpec"/>).</summary>
    internal static ModSettingsItem ForAdmin(string id, AdminSettingSpec spec, ModSettingOptions? options) =>
        new(ModSettingsItemKind.Admin, id)
        {
            Label = spec?.Label ?? "", Admin = spec, Options = options ?? new ModSettingOptions(),
        };

    /// <summary>An admin-channel row's spec, or null.</summary>
    internal AdminSettingSpec? Admin { get; private set; }

    /// <summary>A link row's cell-row key (<c>link-configuration</c>).</summary>
    internal string LinkKey { get; private set; } = "";

    /// <summary>A link row's target place word (<c>configuration</c>).</summary>
    internal string LinkTarget { get; private set; } = "";

    /// <summary>Drop the plugin's delegates (the page registration went).</summary>
    internal void Release()
    {
        // Not Options: that object is the registration's, shared by every build of the page, and
        // the registration drops its predicates when it goes.
        ButtonAction = null;
        InfoValue = null;
    }
}

/// <summary>One section of settings on a mod page: a heading and its rows.</summary>
internal sealed class ModSettingsSection
{
    internal ModSettingsSection(string title, ModSettingsSectionKind kind, List<SettingsRow> rows)
    {
        Title = title ?? "";
        Kind = kind;
        Rows = rows ?? new List<SettingsRow>();
        var items = new List<ModSettingsItem>(Rows.Count);
        for (int i = 0; i < Rows.Count; i++) items.Add(ModSettingsItem.ForRow(Rows[i]));
        Items = items;
    }

    /// <summary>SETTINGS1 stage 2: a section of items (a builder section); <see cref="Rows"/> is its setting rows.</summary>
    internal ModSettingsSection(string title, ModSettingsSectionKind kind, List<ModSettingsItem> items)
    {
        Title = title ?? "";
        Kind = kind;
        Items = items ?? new List<ModSettingsItem>();
        var rows = new List<SettingsRow>(Items.Count);
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Kind == ModSettingsItemKind.Setting && Items[i].Row != null) rows.Add(Items[i].Row!);
        Rows = rows;
    }

    internal string Title { get; }

    internal ModSettingsSectionKind Kind { get; }

    /// <summary>Settings scopes: the scope section the view draws this (sub)section under.</summary>
    internal SettingScope Scope { get; set; } = SettingScope.Personal;
    internal SettingOwner Owner { get; set; } = SettingOwner.Client;

    /// <summary>Settings scopes: the author's title before a scope-restating one was dropped (search and readback).</summary>
    internal string SourceTitle { get; set; } = "";

    /// <summary>The setting rows, in order: what search, reset and the counts act on.</summary>
    internal List<SettingsRow> Rows { get; }

    /// <summary>SETTINGS1 stage 2: everything the section draws, in order (rows, buttons, info lines, dividers).</summary>
    internal List<ModSettingsItem> Items { get; }

    internal static string NameOf(ModSettingsSectionKind kind) => kind switch
    {
        ModSettingsSectionKind.General => "general",
        ModSettingsSectionKind.Keys => "keys",
        ModSettingsSectionKind.Actions => "actions",
        ModSettingsSectionKind.Page => "page",
        _ => "group",
    };
}

/// <summary>
/// SETTINGS1 stage 2 (user redesign 2026-09-29): the settings grid every setting row of a mod page is
/// drawn in. One row per setting: the display name with its help under it in the caption column, the
/// control right-aligned in a <b>fixed-width</b> control column (the same x on every row), and a reset
/// gutter at the right that holds the reset link while the value differs from its default. Nothing
/// overlaps at any help length: the help wraps inside the caption column, which ends a gap before the
/// control column, which ends before the gutter, which ends before the scrollbar band. Offsets are from
/// the row's center, the convention of <see cref="RowColumns"/>.
/// </summary>
internal readonly struct ModSettingsGrid
{
    /// <summary>The control column's width on a row wide enough for it.</summary>
    internal const float ControlWidth = 250f;

    /// <summary>The narrowest the control column gets on a narrow row.</summary>
    internal const float MinControlWidth = 160f;

    /// <summary>The reset gutter: the reset link's square plus its breathing room.</summary>
    internal const float GutterWidth = MenuPanelLayout.IconLinkSize + 8f;

    /// <summary>Between the caption column and the control column.</summary>
    internal const float Gap = 16f;

    /// <summary>Between the control column and the gutter.</summary>
    internal const float GutterGap = 6f;

    /// <summary>The caption column never gets narrower than this.</summary>
    internal const float MinLabelWidth = 120f;

    /// <summary>Help text size (the caption is <see cref="NameFontSize"/>).</summary>
    internal const float HelpFontSize = 15f;

    internal const float NameFontSize = 18f;

    // ---- vertical rhythm (live 2026-09-29: help sat ~12 px under its name and ~8 px above the
    // next row's name, so it read as the next row's) ----------------------------------------------

    /// <summary>The band the name is centered in, at the top of the row.</summary>
    internal const float NameBand = 26f;

    /// <summary>Between the name band and the help line: tight, so the help reads as the name's.</summary>
    internal const float HelpGap = 2f;

    /// <summary>Space a grid row keeps under itself; with the panel's <see cref="PanelLayout.RowGap"/> it is the gap between settings.</summary>
    internal const float RowBottomPad = 8f;

    /// <summary>The height every single-line control is drawn at (field, selector, stepper, keybind, button, segmented picker).</summary>
    internal const float ControlHeight = 34f;

    /// <summary>A switch in a grid row (the status bar's shape).</summary>
    internal const float SwitchWidth = 44f;

    internal const float SwitchHeight = 22f;

    /// <summary>Between the end of one setting and the name of the next.</summary>
    internal static float InterRowGap => RowBottomPad + PanelLayout.RowGap;

    /// <summary>Where the help line starts, from the row's top.</summary>
    internal static float HelpTop(float nameBand) => Math.Max(NameBand, nameBand) + HelpGap;

    /// <summary>
    /// A grid row's height: the name band (at least <see cref="NameBand"/>), the help under it, or
    /// the control when that is taller, plus <see cref="RowBottomPad"/>. Every row, with or without
    /// help, ends the same way, so the pitch between settings is consistent.
    /// </summary>
    internal static float RowHeight(float nameHeight, float helpHeight, float controlHeight)
    {
        float band = Math.Max(NameBand, nameHeight);
        float left = helpHeight > 0f ? HelpTop(band) + helpHeight : band;
        return Math.Max(left, controlHeight) + RowBottomPad;
    }

    /// <summary>
    /// The height the help line may take: its measured height, but never so much that the row would
    /// pass the panel's row cap (<see cref="PanelLayout.MaxRowHeight"/>). Help longer than that is cut
    /// with an ellipsis on screen; the whole text is the readback's <c>helpLine</c>. So a row is
    /// always tall enough for what it draws and nothing overlaps the next row.
    /// </summary>
    internal static float HelpHeightFor(float measured, float nameHeight)
    {
        if (float.IsNaN(measured) || measured <= 0f) return 0f;
        float room = PanelLayout.MaxRowHeight - HelpTop(nameHeight) - RowBottomPad;
        return Math.Max(0f, Math.Min(measured, room));
    }

    /// <summary>Compact controls sit at the column's right edge; everything else fills the column.</summary>
    internal static bool IsCompact(string? runtimeKind) => runtimeKind is "toggle" or "info";

    /// <summary>A control taller than one line (a text area, an options list) keeps its own height.</summary>
    internal static bool IsSingleLine(string? runtimeKind, float builtHeight) =>
        runtimeKind is not ("textarea" or "multiselect") && builtHeight <= UnityUiRuntimeMetrics.RowHeight + 0.5f;

    /// <summary>The height a control is drawn at: <see cref="ControlHeight"/> for every single-line control.</summary>
    internal static float ControlHeightFor(string? runtimeKind, float builtHeight) =>
        IsCompact(runtimeKind) && runtimeKind == "toggle"
            ? SwitchHeight
            : IsSingleLine(runtimeKind, builtHeight) ? ControlHeight : builtHeight;

    /// <summary>A compact control's offset from the row's top, so it is centered on a single-line control's band.</summary>
    internal static float ControlTop(string? runtimeKind, float height) =>
        IsCompact(runtimeKind) ? Math.Max(0f, (ControlHeight - height) * 0.5f) : 0f;

    /// <summary>
    /// Where a control goes in the control column: full-width controls fill it; a compact control
    /// (<paramref name="naturalWidth"/> wide, at most the column) ends on the column's right edge.
    /// </summary>
    internal (float Left, float Width) ControlSlot(string? runtimeKind, float naturalWidth)
    {
        if (!IsCompact(runtimeKind)) return (ControlLeft, ControlColumnWidth);
        float w = naturalWidth > 0f ? Math.Min(naturalWidth, ControlColumnWidth) : ControlColumnWidth;
        return (ControlRight - w, w);
    }

    private ModSettingsGrid(float rowWidth, float labelWidth, float controlWidth, float rightInset)
    {
        RowWidth = rowWidth;
        LabelWidth = labelWidth;
        ControlColumnWidth = controlWidth;
        RightInset = rightInset;
    }

    internal float RowWidth { get; }

    internal float RightInset { get; }

    internal float LabelWidth { get; }

    internal float ControlColumnWidth { get; }

    internal float LabelLeft => -RowWidth * 0.5f;

    internal float LabelRight => LabelLeft + LabelWidth;

    internal float ControlLeft => LabelRight + Gap;

    internal float ControlRight => ControlLeft + ControlColumnWidth;

    internal float GutterLeft => ControlRight + GutterGap;

    internal float GutterRight => GutterLeft + GutterWidth;

    internal float GutterCentreX => GutterLeft + GutterWidth * 0.5f;

    /// <summary>The caption and control columns as the kit's <see cref="RowColumns"/>.</summary>
    internal RowColumns Columns => new(
        LabelWidth, LabelLeft + LabelWidth * 0.5f, ControlColumnWidth, ControlLeft + ControlColumnWidth * 0.5f);

    /// <summary>The grid for a row <paramref name="rowWidth"/> wide.</summary>
    internal static ModSettingsGrid For(float rowWidth)
    {
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        float inset = HeadBlockLayout.RightColumnInset;
        float usable = rowWidth - inset - GutterWidth - GutterGap - Gap;
        float control = Math.Min(ControlWidth, Math.Max(MinControlWidth, usable * 0.45f));
        float label = usable - control;
        if (label < MinLabelWidth)
        {
            // A very narrow pane: split what is there rather than let a column go negative.
            label = Math.Max(1f, usable * 0.5f);
            control = Math.Max(1f, usable - label);
        }

        return new ModSettingsGrid(rowWidth, label, control, inset);
    }
}

/// <summary>MENUKIT2: how a control sits in a game-look settings row (<see cref="ModSettingsRowGrid"/>).</summary>
internal enum SettingsControlShape
{
    /// <summary>The game's checkbox, at the compact right edge (a bool setting).</summary>
    Checkbox = 0,

    /// <summary>A bar filling the control column to the bar edge: dropdown, text, keybind, read-only value, request field.</summary>
    Bar = 1,

    /// <summary>A slider track from the column start and its value box at the compact right edge.</summary>
    SliderWithValue = 2,

    /// <summary>A value box alone at the compact right edge: a number stepper, a color swatch.</summary>
    ValueBox = 3,

    /// <summary>The game's row button (<c>SimpleMenuButton</c>), right-aligned to the bar edge.</summary>
    Button = 4,

    /// <summary>A control taller than one line (an options list, a text area): keeps its own height.</summary>
    Tall = 5,

    /// <summary>A keybind: the Keybinds tab's key cap, right-aligned at the bar edge.</summary>
    KeyCap = 6,
}

/// <summary>
/// MENUKIT2 (decided 2026-09-30): the
/// game-look settings grid, the Options page's row as probe M1 read it, converted to the workspace's
/// units with <see cref="GameSettingsLook.Kit"/>. One line per setting at a fixed
/// <see cref="RowPitch"/>; the help moves to the description column (<see cref="ModSettingsDescription"/>).
///
/// <para>Columns, as x from the row's <b>left edge</b> (0 .. <see cref="RowWidth"/>): the name from
/// <see cref="NameInset"/> to the control column; the control column from <see cref="ControlLeft"/>
/// (<see cref="GameSettingsLook.ControlStartShare"/> of the usable width); bars end at
/// <see cref="BarRight"/>, compact controls (checkbox, value box) at <see cref="CompactRight"/>; the reset
/// arrow is centered at <see cref="ResetCenterX"/>, in the gutter before the scrollbar band.</para>
///
/// <para>Not drawn yet: the runtime still draws <see cref="ModSettingsGrid"/> until the page's drawing is
/// switched to this grid.</para>
/// </summary>
internal readonly struct ModSettingsRowGrid
{
    /// <summary>The fixed row pitch: the game's 58-unit entry (70).</summary>
    internal static float RowPitch => GameSettingsLook.Kit(GameSettingsLook.RowHeight);

    /// <summary>The setting name's size (the game's 20) and left inset (16).</summary>
    internal static float NameFontSize => GameSettingsLook.NameText;

    internal static float NameInset => GameSettingsLook.Kit(GameSettingsLook.NameLeft);

    /// <summary>The <c>ListButton_TopGradient</c> strip at every row's top: the resting separator (16 units high).</summary>
    internal static float TopStripHeight => GameSettingsLook.Kit(16f);

    /// <summary>
    /// The checkbox's sprite square (24; the frame drawn inside it is about 18-20 px, as the game's live)
    /// and its mark (20). Round C: drawn at the game's own units, not scaled; scaled they read too large.
    /// </summary>
    internal static float CheckboxSize => GameSettingsLook.CheckboxSize;

    internal static float CheckmarkSize => GameSettingsLook.CheckmarkSize;

    /// <summary>A keybind's key cap (the Keybinds tab's cap look, sized like a control).</summary>
    internal const float KeyCapWidth = 180f;

    internal const float KeyCapHeight = 36f;

    /// <summary>Round D: a row chip is a small bordered chip immediately left of the row's value or control.</summary>
    internal const float ChipHeight = 22f;

    internal const float ChipGap = 10f;

    internal const float ChipPad = 10f;

    /// <summary>A chip's width for its caption's estimated width (padded, at least 56).</summary>
    internal static float ChipWidth(float textWidth) =>
        Math.Max(56f, (float.IsNaN(textWidth) ? 0f : Math.Max(0f, textWidth)) + 2f * ChipPad);

    internal static float ResetSize => GameSettingsLook.Kit(GameSettingsLook.ResetSize);

    internal static float BarHeight => GameSettingsLook.Kit(GameSettingsLook.DropdownHeight);

    internal static float ButtonHeight => GameSettingsLook.Kit(GameSettingsLook.ButtonHeight);

    internal static float ButtonWidth => GameSettingsLook.Kit(GameSettingsLook.ButtonWidth);

    internal static float ValueBoxWidth => GameSettingsLook.Kit(GameSettingsLook.ValueBoxWidth);

    internal static float ValueBoxHeight => GameSettingsLook.Kit(GameSettingsLook.ValueBoxHeight);

    internal static float SliderHeight => GameSettingsLook.Kit(GameSettingsLook.SliderHeight);

    internal static float SliderTrackHeight => GameSettingsLook.Kit(GameSettingsLook.SliderTrackHeight);

    internal static float SliderHandleSize => GameSettingsLook.Kit(GameSettingsLook.SliderHandleSize);

    internal static float ModifiedBarWidth => GameSettingsLook.Kit(GameSettingsLook.ModifiedBarWidth);

    /// <summary>The narrowest the control column gets, and the narrowest the name column gets, before both split what is left.</summary>
    internal const float MinControlWidth = 250f;

    internal const float MinNameWidth = 140f;

    /// <summary>The value box, checkbox and swatch stay this far in from the bar edge (the game's 112 against 96).</summary>
    internal static float CompactInset => GameSettingsLook.Kit(GameSettingsLook.ValueBoxRightGap - GameSettingsLook.DropdownRightGap);

    /// <summary>
    /// CONTROLS1: a slider's value box and the gap before it. The game's 112-unit box and 32-unit gap sit
    /// beside a 1500-pixel track on its full-width Options page; on our narrower rows they took nearly half
    /// the control column and left a stub of a track (live 2026-10-03: a 157-pixel track beside a 135-pixel
    /// box holding "2"). The box keeps the game's frame and height and is sized for a short number.
    /// </summary>
    internal static float SliderValueBoxWidth => GameSettingsLook.Kit(72f);

    internal static float SliderValueGap => GameSettingsLook.Kit(16f);

    /// <summary>
    /// GRIDROW1: a slider's track width in a slot <paramref name="slotWidth"/> wide (track and value box together): the slot
    /// less the value box and its gap while the column has room for the box (<paramref name="valueBoxRoom"/>), else the
    /// whole slot. A plain slider takes the same width as a slider field's track, so the two end at one x.
    /// </summary>
    internal static float SliderTrackWidth(float slotWidth, bool valueBoxRoom) =>
        valueBoxRoom ? Math.Max(1f, slotWidth - SliderValueBoxWidth - SliderValueGap) : Math.Max(1f, slotWidth);

    private ModSettingsRowGrid(float rowWidth, float rightInset, float controlLeft, float barRight, float compactInset = -1f)
    {
        RowWidth = rowWidth;
        RightInset = rightInset;
        ControlLeft = controlLeft;
        BarRight = barRight;
        _compactInset = compactInset;
    }

    private readonly float _compactInset;

    internal float RowWidth { get; }

    /// <summary>The scrollbar band kept free at the right (<see cref="HeadBlockLayout.RightColumnInset"/>).</summary>
    internal float RightInset { get; }

    /// <summary>The usable row's right edge: the row less the scrollbar band.</summary>
    internal float Right => RowWidth - RightInset;

    internal float ControlLeft { get; }

    /// <summary>Where bars (dropdown, text, keybind, button) end: the game's 96 units short of the right.</summary>
    internal float BarRight { get; }

    /// <summary>Where compact controls (checkbox, value box, swatch) end.</summary>
    internal float CompactRight => BarRight - (_compactInset >= 0f ? _compactInset : CompactInset);

    internal float ControlWidth => BarRight - ControlLeft;

    /// <summary>The name column: from the inset to a gap before the control column; the name is cut with an ellipsis past it.</summary>
    internal float NameWidth => Math.Max(1f, ControlLeft - NameInset - Gap);

    /// <summary>Between the name column and the control column.</summary>
    internal const float Gap = 16f;

    /// <summary>The reset arrow's center (the game's 32 units from the right).</summary>
    internal float ResetCenterX => Right - GameSettingsLook.Kit(GameSettingsLook.ResetCenterFromRight);

    /// <summary>The checkbox's center (the game's 128 units from the right, i.e. on the compact edge).</summary>
    internal float CheckboxCenterX => CompactRight - CheckboxSize * 0.5f;

    /// <summary>The grid for a row <paramref name="rowWidth"/> wide.</summary>
    internal static ModSettingsRowGrid For(float rowWidth)
    {
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        float inset = HeadBlockLayout.RightColumnInset;
        float right = rowWidth - inset;
        float barRight = right - GameSettingsLook.Kit(GameSettingsLook.DropdownRightGap);
        float controlLeft = right * GameSettingsLook.ControlStartShare;
        // A narrow row: the control column keeps its floor while the name keeps its own; below both,
        // they split what is there.
        if (barRight - controlLeft < MinControlWidth) controlLeft = barRight - MinControlWidth;
        float nameRoom = controlLeft - NameInset - Gap;
        if (nameRoom < MinNameWidth)
        {
            float usable = Math.Max(2f, barRight - NameInset - Gap);
            controlLeft = NameInset + Gap + usable * 0.5f;
        }

        return new ModSettingsRowGrid(rowWidth, inset, controlLeft, barRight);
    }

    /// <summary>
    /// WINDOWS1 live fix: the grid of a standard window's row. A window has no reset arrows, so the gutter the Options page
    /// keeps for them (bars ending 96 short of the right, compact controls 16 further in) left the checkbox mid-row; here
    /// bars and compact controls end at the row's right edge, less the scrollbar band.
    /// </summary>
    internal static ModSettingsRowGrid ForWindow(float rowWidth)
    {
        var g = For(rowWidth);
        float barRight = g.Right;
        float controlLeft = Math.Min(g.ControlLeft, barRight - MinControlWidth);
        if (controlLeft - NameInset - Gap < MinNameWidth) controlLeft = g.ControlLeft;
        return new ModSettingsRowGrid(g.RowWidth, g.RightInset, controlLeft, barRight, 0f);
    }

    /// <summary>The shape each drawn control takes in the game-look row.</summary>
    internal static SettingsControlShape ShapeOf(SettingControl control) => control switch
    {
        SettingControl.Toggle => SettingsControlShape.Checkbox,
        SettingControl.Slider => SettingsControlShape.SliderWithValue,
        SettingControl.Number or SettingControl.Color => SettingsControlShape.ValueBox,
        SettingControl.Action => SettingsControlShape.Button,
        SettingControl.Keybind => SettingsControlShape.KeyCap,
        SettingControl.Radio or SettingControl.MultiSelect or SettingControl.TextArea => SettingsControlShape.Tall,
        // Dropdown, Segmented (2-4 short options drawn as the game draws every one-of choice: a bar),
        // Text, Keybind, ServerRequest, ReadOnly.
        _ => SettingsControlShape.Bar,
    };

    /// <summary>
    /// Where the main control goes: x from the row's left edge, width and height. A slider's slot is its
    /// track; <see cref="ValueBoxSlot"/> is its value box.
    /// </summary>
    internal (float Left, float Width, float Height) ControlSlot(SettingsControlShape shape, float tallHeight = 0f)
    {
        switch (shape)
        {
            case SettingsControlShape.Checkbox:
                return (CompactRight - CheckboxSize, CheckboxSize, CheckboxSize);
            case SettingsControlShape.ValueBox:
                return ValueBoxSlot;
            case SettingsControlShape.SliderWithValue:
            {
                if (!SliderHasValueBox) return (ControlLeft, Math.Max(1f, CompactRight - ControlLeft), SliderHeight);
                float trackRight = CompactRight - SliderValueBoxWidth - SliderValueGap;
                return (ControlLeft, trackRight - ControlLeft, SliderHeight);
            }
            case SettingsControlShape.Button:
            {
                float w = Math.Min(ButtonWidth, ControlWidth);
                return (BarRight - w, w, ButtonHeight);
            }
            case SettingsControlShape.Tall:
                return (ControlLeft, ControlWidth, Math.Max(BarHeight, tallHeight));
            case SettingsControlShape.KeyCap:
            {
                float w = Math.Min(KeyCapWidth, ControlWidth);
                return (BarRight - w, w, KeyCapHeight);
            }
            default:
                return (ControlLeft, ControlWidth, BarHeight);
        }
    }

    /// <summary>
    /// Whether a runtime text-field row draws its caption: every settings-grid field does, inline (Apply at its right) or
    /// not; a search field and a plain inline field outside the grid (the Configuration folder path) do not (live
    /// 2026-10-06: inline grid fields lost their names).
    /// </summary>
    internal static bool InputDrawsCaption(bool searchField, bool inline, bool grid) => !searchField && (grid || !inline);

    /// <summary>
    /// A right-aligned read-only value's drawn left edge (x from the row's center): the control's right edge less the text's
    /// width, never past the control's own left edge.
    /// </summary>
    internal static float ReadOnlyValueLeft(float controlRight, float controlWidth, float textWidth) =>
        textWidth > 0f ? controlRight - Math.Min(controlWidth, textWidth) : controlRight - controlWidth;

    /// <summary>The row chip's center x (from the row's center): immediately left of the value or control, one gap away.</summary>
    internal static float ChipCenterX(float valueLeft, float chipWidth) => valueLeft - ChipGap - chipWidth * 0.5f;

    /// <summary>Settings organization review: an inline button's width (Apply) beside its field, in game units.</summary>
    internal const float InlineButtonGameWidth = 110f;

    /// <summary>How far left of the reset arrow's center the inline buttons stop, in game units (the arrow's half and a gap).</summary>
    internal const float InlineResetClearance = 28f;

    /// <summary>
    /// Where inline buttons end (live 2026-10-06: the Server name field beside Apply was too narrow): past
    /// <see cref="BarRight"/>, into the gutter, stopping short of the reset arrow, so the field keeps nearly the whole bar.
    /// </summary>
    internal float InlineRight => Math.Max(BarRight, ResetCenterX - GameSettingsLook.Kit(InlineResetClearance));

    /// <summary>Between an inline field and its button, and between inline buttons.</summary>
    internal const float InlineGap = 8f;

    /// <summary>The shortest field kept beside inline buttons; below it the buttons narrow.</summary>
    internal const float MinInlineFieldWidth = 80f;

    /// <summary>
    /// Settings organization review (live: Apply sat on a separate full-width row): a one-line field and its
    /// <paramref name="buttons"/> inline buttons share the control column — the field from the column's start, the buttons at
    /// its right end (<see cref="InlineRight"/>). Returns the field slot and button <paramref name="index"/>'s slot, x from the
    /// row's left edge.
    /// </summary>
    internal (float FieldLeft, float FieldWidth, float ButtonLeft, float ButtonWidth) InlineSlots(int buttons, int index)
    {
        int n = Math.Max(1, buttons);
        float column = InlineRight - ControlLeft;
        float buttonW = GameSettingsLook.Kit(InlineButtonGameWidth);
        float needed = n * (buttonW + InlineGap);
        if (column - needed < MinInlineFieldWidth)
            buttonW = Math.Max(1f, (column - MinInlineFieldWidth) / n - InlineGap);
        float fieldW = Math.Max(1f, column - n * (buttonW + InlineGap));
        int i = Math.Max(0, Math.Min(n - 1, index));
        float buttonLeft = ControlLeft + fieldW + InlineGap + i * (buttonW + InlineGap);
        return (ControlLeft, fieldW, buttonLeft, buttonW);
    }

    /// <summary>The shortest slider track worth drawing beside its value box.</summary>
    internal const float MinSliderTrack = 80f;

    /// <summary>
    /// A slider draws its value box when the column holds the box and a <see cref="MinSliderTrack"/> track;
    /// on a narrower column the track takes the column and the value is read back only.
    /// </summary>
    internal bool SliderHasValueBox => CompactRight - SliderValueBoxWidth - SliderValueGap - ControlLeft >= MinSliderTrack;

    /// <summary>The value box of a number or color: at the compact edge.</summary>
    internal (float Left, float Width, float Height) ValueBoxSlot => (CompactRight - ValueBoxWidth, ValueBoxWidth, ValueBoxHeight);

    /// <summary>A slider's value box: at the compact edge, <see cref="SliderValueBoxWidth"/> wide.</summary>
    internal (float Left, float Width, float Height) SliderValueBoxSlot =>
        (CompactRight - SliderValueBoxWidth, SliderValueBoxWidth, ValueBoxHeight);

    /// <summary>
    /// A row's height: the fixed pitch, whatever its help says (the help is in the description column).
    /// A tall control (an options list, a text area) takes its height plus the pitch's air.
    /// </summary>
    internal static float RowHeight(SettingsControlShape shape, float tallHeight = 0f)
    {
        if (shape != SettingsControlShape.Tall || float.IsNaN(tallHeight) || tallHeight <= BarHeight) return RowPitch;
        return Math.Min(PanelLayout.MaxRowHeight, tallHeight + (RowPitch - BarHeight));
    }

    /// <summary>
    /// The height a grid row's object is drawn at: its pitch less the panel's row gap, which the layout
    /// cursor adds after every row (so row tops land exactly one pitch apart). The row's band reaches
    /// down over the gap (<see cref="BandOverhang"/>), so bands touch.
    /// </summary>
    internal static float DrawnRowHeight(SettingsControlShape shape, float tallHeight = 0f) =>
        Math.Max(1f, RowHeight(shape, tallHeight) - PanelLayout.RowGap);

    /// <summary>How far a row's band reaches below the row object, over the layout's row gap.</summary>
    internal const float BandOverhang = PanelLayout.RowGap;

    /// <summary>The shape of a runtime row kind (the runtime's <c>kind</c> strings).</summary>
    internal static SettingsControlShape ShapeOfKind(string? runtimeKind) => runtimeKind switch
    {
        "toggle" => SettingsControlShape.Checkbox,
        "slider-entry" or "slider" => SettingsControlShape.SliderWithValue,
        "numeric" or "color" => SettingsControlShape.ValueBox,
        "button" => SettingsControlShape.Button,
        "keybind" => SettingsControlShape.KeyCap,
        "textarea" or "radio" or "multiselect" => SettingsControlShape.Tall,
        _ => SettingsControlShape.Bar,
    };

    /// <summary>
    /// The name and control columns as the kit's <see cref="RowColumns"/> (offsets from the row's
    /// center, the runtime's convention). A row with no control gives the name everything up to the
    /// reset gutter.
    /// </summary>
    internal RowColumns Columns(bool hasControl)
    {
        float left = -RowWidth * 0.5f;
        if (!hasControl)
        {
            float w = Math.Max(1f, BarRight - NameInset);
            return new RowColumns(w, left + NameInset + w * 0.5f, 0f, 0f);
        }

        return new RowColumns(NameWidth, left + NameInset + NameWidth * 0.5f, ControlWidth, left + ControlLeft + ControlWidth * 0.5f);
    }

    /// <summary>An x from the row's left edge as an offset from its center.</summary>
    internal float FromCenter(float xFromLeft) => xFromLeft - RowWidth * 0.5f;

    // ---- section header (CustomGameSettings_Label) -------------------------------------------------

    /// <summary>The section header: title size (the game's 32), row height (48), and the rule under the title's left half.</summary>
    internal static float HeaderFontSize => GameSettingsLook.SectionTitleText;

    /// <summary>
    /// The section header's row: the title middle-aligned (a bottom-aligned cell is capped at 30 units
    /// and cut a larger title away entirely, live round C), the rule directly under it.
    /// </summary>
    internal static float HeaderHeight => GameSettingsLook.HeaderHeight;

    /// <summary>The page's small action row above the sections: "Settings", the search and Reset all.</summary>
    internal const float ActionRowHeight = 40f;

    internal const float HeaderRuleHeight = GameSettingsLook.HeaderRuleHeight;

    internal float HeaderRuleWidth => Math.Max(1f, Right * GameSettingsLook.HeaderRuleShare);
}

/// <summary>MENUKIT2: the reset arrow's state in the gutter (the game dims it at the default).</summary>
internal enum SettingsResetState
{
    /// <summary>The row can never be reset here (a server-owned, read-only or action row): no arrow.</summary>
    None = 0,

    /// <summary>At its default: the arrow is drawn dim and does nothing.</summary>
    Default = 1,

    /// <summary>Changed: the arrow is the game's steel blue and resets on click.</summary>
    Changed = 2,
}

/// <summary>MENUKIT2: a row name's tone.</summary>
internal enum SettingsNameTone
{
    /// <summary>At rest: the game's 161 gray.</summary>
    Rest = 0,

    /// <summary>Hovered or selected: the game's 231.</summary>
    Lit = 1,

    /// <summary>A keybind in conflict: the warning tone (the sentence is in the description column).</summary>
    Warn = 2,
}

/// <summary>
/// MENUKIT2: what a one-line row shows without text, now the sentences are in the description column
/// (study section 3, change 6): changed is the bright reset arrow, a pending restart a small chip,
/// grayed the row at <see cref="GreyedAlpha"/>, a conflict a warning-toned name. Game-free.
/// </summary>
internal readonly struct ModSettingsRowMarks
{
    /// <summary>A grayed row's alpha (<c>UnityUiRuntime</c>'s <c>GreyedAlpha</c>).</summary>
    internal const float GreyedAlpha = 0.45f;

    internal const string RestartChip = "Restart";

    internal const string ServerChip = "Server";

    internal const string AwaitingServerChip = "Awaiting server";

    /// <summary>
    /// The small chip at the end of a row's name, or empty: <c>Restart</c> while a needs-restart change
    /// waits; on a server-synced value <c>Server</c> (the server's value is in force) or <c>Awaiting
    /// server</c> (not received yet) — in place of the old "(awaiting server synchronization)" suffix.
    /// </summary>
    internal static string ChipText(bool pendingRestart, bool synced, bool locked) =>
        pendingRestart ? RestartChip : !synced ? "" : locked ? ServerChip : AwaitingServerChip;

    /// <summary>Settings organization review (live): the small marker on a row the server's lock list keeps out of the game.</summary>
    internal const string LockedChip = "Locked";

    /// <summary>An admin-channel row's chip: <c>Locked</c> while the lock list names it, <c>Restart</c> while a stored value waits.</summary>
    internal static string AdminChipText(AdminSettingSpec? spec)
    {
        if (spec == null) return "";
        if (spec.LockedByNow() != null) return LockedChip;
        if (spec.PendingNow()) return RestartChip;
        // Live 2026-10-06: a row a non-admin only reads carries the Server chip, as every other server row does.
        return spec.ViewOnlyNow() ? ServerChip : "";
    }

    internal static string ChipTextOf(SettingsRow? row, ModSettingOptions? options)
    {
        if (row == null) return "";
        bool synced = false, locked = false;
        try
        {
            synced = row.IsSynced || row.IsSyncedNow;
            if (synced) locked = ConfigSync.IsLocked(row.PluginId, row.Section, row.Key);
        }
        catch { synced = false; }
        return ChipText(ModSettingsLayout.IsPendingRestart(row, options), synced, locked);
    }

    internal ModSettingsRowMarks(SettingsResetState reset, bool pendingRestart, bool greyed, bool conflict, bool lit)
    {
        Reset = reset;
        PendingRestart = pendingRestart;
        Greyed = greyed;
        Conflict = conflict;
        Lit = lit;
    }

    internal SettingsResetState Reset { get; }

    internal bool PendingRestart { get; }

    internal bool Greyed { get; }

    internal bool Conflict { get; }

    /// <summary>Hovered or selected.</summary>
    internal bool Lit { get; }

    internal float RowAlpha => Greyed ? GreyedAlpha : 1f;

    internal SettingsNameTone NameTone => Conflict ? SettingsNameTone.Warn : Lit ? SettingsNameTone.Lit : SettingsNameTone.Rest;

    /// <summary>The name's gray (0..1) for the rest and lit tones (<see cref="GameSettingsLook.NameGrey"/>).</summary>
    internal float NameGrey => GameSettingsLook.NameGrey(Lit);

    /// <summary>The reset arrow's color: the game's steel blue when changed (white hovered), dark gray at the default.</summary>
    internal (float R, float G, float B, float A) ResetColor(bool hovered) => Reset switch
    {
        SettingsResetState.Changed => hovered ? GameSettingsLook.ResetHover : GameSettingsLook.ResetRest,
        _ => GameSettingsLook.ResetDisabled,
    };

    /// <summary>The marks for a setting row.</summary>
    internal static ModSettingsRowMarks For(SettingsRow? row, ModSettingOptions? options, bool enabled, bool lit)
    {
        if (row == null) return new ModSettingsRowMarks(SettingsResetState.None, false, !enabled, false, lit);
        var reset = !ModSettingsLayout.CanEverReset(row) ? SettingsResetState.None
            : ModSettingsLayout.CanReset(row) ? SettingsResetState.Changed : SettingsResetState.Default;
        bool conflict = row.Kind == SettingsEditorKind.Keybind && ModSettingsLayout.KeyConflictLineOf(row).Length > 0;
        return new ModSettingsRowMarks(reset, ModSettingsLayout.IsPendingRestart(row, options), !enabled, conflict, lit);
    }

    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["reset"] = Reset switch { SettingsResetState.Changed => "changed", SettingsResetState.Default => "default", _ => "none" },
        ["restartChip"] = PendingRestart,
        ["greyed"] = Greyed,
        ["nameTone"] = NameTone switch { SettingsNameTone.Warn => "warn", SettingsNameTone.Lit => "lit", _ => "rest" },
    };
}

/// <summary>Which control a settings row is drawn with on the mod page.</summary>
internal enum SettingControl
{
    ReadOnly = 0,
    Toggle = 1,
    Text = 2,
    Number = 3,
    Slider = 4,
    Dropdown = 5,
    Color = 6,
    Keybind = 7,
    Action = 8,

    /// <summary>A synced entry opted into admin editing: field, Apply, status note (docs/config-sync.md).</summary>
    ServerRequest = 9,

    /// <summary>SETTINGS1 stage 2: a one-of choice with every option visible (builder <c>.Radio</c>).</summary>
    Radio = 10,

    /// <summary>SETTINGS1 stage 2: a set of choices — a <c>[Flags]</c> enum or a comma-separated string list.</summary>
    MultiSelect = 11,

    /// <summary>SETTINGS1 stage 2: multi-line text (builder <c>.Text(entry, multiline: true)</c>).</summary>
    TextArea = 12,

    /// <summary>
    /// SETTINGS1 stage 2: a finite choice of two to four short options, drawn as a segmented picker
    /// instead of a selector. Only <see cref="ModSettingsLayout.DrawnControl"/> answers this.
    /// </summary>
    Segmented = 13,

    /// <summary>
    /// JOINFLOW1: a date and time stored as ISO 8601 UTC in a string entry (builder <c>.DateTime</c>),
    /// drawn as the date-time field: the value box and a button that opens the picker.
    /// </summary>
    DateTime = 14,
}

/// <summary>Bounds, step, decimals and units of a numeric row.</summary>
internal readonly struct SettingNumberSpec
{
    internal SettingNumberSpec(double min, double max, double step, int decimals, string units, bool bounded, bool integral)
    {
        Min = min;
        Max = max;
        Step = step;
        Decimals = decimals;
        Units = units ?? "";
        Bounded = bounded;
        Integral = integral;
    }

    internal double Min { get; }
    internal double Max { get; }
    internal double Step { get; }
    internal int Decimals { get; }
    internal string Units { get; }

    /// <summary>Both a minimum and a maximum were declared (an <c>AcceptableValueRange</c> or presentation bounds).</summary>
    internal bool Bounded { get; }

    /// <summary>An integer type: steps of whole numbers, written back without a decimal point.</summary>
    internal bool Integral { get; }

    /// <summary>
    /// The invariant text a committed number is written back as. The value is snapped to
    /// <see cref="Step"/> on the same grid as the stepper (<see cref="UiNumericParse.Snap"/>: from
    /// <see cref="Min"/> unless it is a type bound too large to keep the grid, then from zero) and rounded to
    /// <see cref="Decimals"/> <b>in double</b> before formatting, so a slider's widened float
    /// (<c>0.3f</c> is <c>0.30000001192092896</c> as a double) is written as <c>0.3</c> and never
    /// as its noise (review finding 1).
    /// </summary>
    internal string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        if (Integral)
            return Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        double origin = Math.Abs(Min) <= 1e15 ? Min : 0;
        if (Step > 0 && Math.Abs(origin) < 1e15)
        {
            double snapped = origin + Math.Round((value - origin) / Step, MidpointRounding.AwayFromZero) * Step;
            if (!double.IsNaN(snapped) && !double.IsInfinity(snapped)) value = snapped;
        }

        value = Math.Round(value, Math.Max(0, Math.Min(15, Decimals)), MidpointRounding.AwayFromZero);
        if (value == 0) value = 0; // no "-0"
        return SettingsEditors.Format(value);
    }
}

/// <summary>
/// SETTINGS1 (docs/mod-settings.md): everything about the settings a mod page shows inline that can
/// be decided without Unity — the sections, the control each row gets, its bounds, whether it is
/// changed from its default, the search threshold and the filter. The page (<see cref="ModSettingsView"/>)
/// draws what this decides; the readback reports the same values, so the two cannot disagree.
/// </summary>
internal static class ModSettingsLayout
{
    /// <summary>
    /// LISTING1 (user review 2026-10-06: "Room left looks like an option"): whether a row lights like a setting when it
    /// is hovered or selected. An info or status line (a counter, Countdown now, Status) does not (a setting the viewer
    /// may only read still lights: it is a setting); it still describes itself in the column, without the band.
    /// </summary>
    internal static bool TakesHighlight(ModSettingsItemKind kind, SettingControl control) =>
        kind != ModSettingsItemKind.Info;

    /// <summary>More rows than this and the page shows a search field above the sections.</summary>
    internal const int SearchThreshold = 10;

    internal const string GeneralTitle = "General";
    internal const string KeysTitle = "Keys";
    internal const string ActionsTitle = "Actions";


    /// <summary>Decimals a number field ever shows.</summary>
    internal const int MaxDecimals = 6;

    internal static bool ShowSearch(int rowCount) => rowCount > SearchThreshold;

    /// <summary>
    /// The sections, in order: every registered group (its entries as the mod listed them), then
    /// <see cref="GeneralTitle"/> for the mod's other config entries, <see cref="KeysTitle"/> for its
    /// keybinds and <see cref="ActionsTitle"/> for its registered actions. A row appears once: a group
    /// claims an entry before the catalog's own walk sees it. Empty sections are dropped.
    /// </summary>
    internal static List<ModSettingsSection> Build(
        IReadOnlyList<(string Title, IReadOnlyList<SettingsRow> Rows)>? groups,
        IReadOnlyList<SettingsRow>? pluginRows)
    {
        var sections = new List<ModSettingsSection>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (groups != null)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                var rows = new List<SettingsRow>();
                var source = groups[i].Rows;
                if (source != null)
                {
                    for (int j = 0; j < source.Count; j++)
                    {
                        var row = source[j];
                        if (row == null || !claimed.Add(row.RowId)) continue;
                        rows.Add(row);
                    }
                }

                if (rows.Count > 0) sections.Add(new ModSettingsSection(groups[i].Title, ModSettingsSectionKind.Group, rows));
            }
        }

        var general = new List<SettingsRow>();
        var keys = new List<SettingsRow>();
        var actions = new List<SettingsRow>();
        if (pluginRows != null)
        {
            for (int i = 0; i < pluginRows.Count; i++)
            {
                var row = pluginRows[i];
                if (row == null || !claimed.Add(row.RowId)) continue;
                switch (row.Kind)
                {
                    case SettingsEditorKind.Keybind: keys.Add(row); break;
                    case SettingsEditorKind.Action: actions.Add(row); break;
                    default: general.Add(row); break;
                }
            }
        }

        if (general.Count > 0) sections.Add(new ModSettingsSection(GeneralTitle, ModSettingsSectionKind.General, general));
        if (keys.Count > 0) sections.Add(new ModSettingsSection(KeysTitle, ModSettingsSectionKind.Keys, keys));
        if (actions.Count > 0) sections.Add(new ModSettingsSection(ActionsTitle, ModSettingsSectionKind.Actions, actions));
        return sections;
    }

    // ---- MENUKIT2 round C: the raw "Settings and keybinds" page in the game look -------------------------

    /// <summary>
    /// The raw settings page (every config entry and keybind of one mod, <c>mods_ui select page=settings</c>)
    /// as game-look sections: one section per config section, in the order the entries come; each row's
    /// name is its key humanized (<see cref="DisplayName"/>), and its description is the entry's description
    /// plus what it accepts (<see cref="RawHelp"/>). The control is the automatic one
    /// (<see cref="ControlFor(SettingsRow)"/>): checkbox, slider and value box, dropdown bar, key cap, text box.
    /// </summary>
    internal static List<ModSettingsSection> RawSections(IReadOnlyList<SettingsRow>? rows)
    {
        var order = new List<string>();
        var bySection = new Dictionary<string, List<ModSettingsItem>>(StringComparer.Ordinal);
        if (rows != null)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null) continue;
                string title = string.IsNullOrWhiteSpace(row.Section) ? GeneralTitle : DisplayName(row.Section.Trim());
                if (!bySection.TryGetValue(title, out var items))
                {
                    items = new List<ModSettingsItem>();
                    bySection[title] = items;
                    order.Add(title);
                }

                items.Add(ModSettingsItem.ForRow(row, new ModSettingOptions { Help = RawHelp(row) }));
            }
        }

        var sections = new List<ModSettingsSection>(order.Count);
        foreach (var title in order) sections.Add(new ModSettingsSection(title, ModSettingsSectionKind.Group, bySection[title]));
        return sections;
    }

    /// <summary>
    /// A raw row's description: the entry's own description, then what it accepts
    /// (<c>One of: Server default, 24-hour, 12-hour.</c>, <c>From 1 to 10.</c>) — the bounds that used to be
    /// bracketed onto the caption.
    /// </summary>
    internal static string RawHelp(SettingsRow row)
    {
        if (row == null) return "";
        string description = "";
        string bounds = "";
        try { description = row.Description ?? ""; } catch { description = ""; }
        try { bounds = row.Bounds ?? ""; } catch { bounds = ""; }
        string accepts = AcceptsSentence(bounds);
        string help = Sentence(description);
        if (accepts.Length == 0) return help;
        return help.Length == 0 ? accepts : help + " " + accepts;
    }

    /// <summary>A row's bounds text as a sentence: <c>one of: a, b</c> → <c>One of: a, b.</c>; <c>1 to 10</c> → <c>From 1 to 10.</c></summary>
    internal static string AcceptsSentence(string? bounds)
    {
        string b = (bounds ?? "").Trim();
        if (b.Length == 0) return "";
        if (b.StartsWith("one of:", StringComparison.OrdinalIgnoreCase)) return Sentence("One of:" + b.Substring("one of:".Length));
        if (char.IsDigit(b[0]) || b[0] == '-' || b[0] == '.') return Sentence("From " + b);
        return Sentence(char.ToUpperInvariant(b[0]) + b.Substring(1));
    }

    internal static int RowCount(IReadOnlyList<ModSettingsSection>? sections)
    {
        int n = 0;
        if (sections == null) return 0;
        for (int i = 0; i < sections.Count; i++) n += sections[i].Rows.Count;
        return n;
    }

    /// <summary>
    /// The sections with only the rows <paramref name="query"/> matches; a section whose title
    /// matches keeps all its rows. An empty query returns the sections unchanged.
    /// </summary>
    internal static List<ModSettingsSection> Filter(IReadOnlyList<ModSettingsSection>? sections, string? query)
    {
        var result = new List<ModSettingsSection>();
        if (sections == null) return result;
        string q = (query ?? "").Trim();
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (q.Length == 0 || Contains(section.Title, q))
            {
                result.Add(section);
                continue;
            }

            var rows = new List<SettingsRow>();
            for (int j = 0; j < section.Rows.Count; j++)
                if (Matches(section.Rows[j], q)) rows.Add(section.Rows[j]);
            if (rows.Count > 0) result.Add(new ModSettingsSection(section.Title, section.Kind, rows));
        }

        return result;
    }

    /// <summary>Case-insensitive match on the caption, the cfg key and section, and the help text.</summary>
    internal static bool Matches(SettingsRow row, string? query)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0) return true;
        if (row == null) return false;
        return Contains(row.Label, q) || Contains(DisplayNameOf(row), q) || Contains(row.Key, q)
            || Contains(row.Section, q) || Contains(row.Description, q);
    }

    /// <summary>SETTINGS1 stage 2: a search match for any item — a row by its fields and builder label/help, a button or info line by its label and help.</summary>
    internal static bool Matches(ModSettingsItem item, string? query)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0) return true;
        if (item == null) return false;
        switch (item.Kind)
        {
            case ModSettingsItemKind.Setting:
                return item.Row != null
                    && (Matches(item.Row, q) || Contains(item.Options.Label, q) || Contains(item.Options.Help, q));
            case ModSettingsItemKind.Button:
            case ModSettingsItemKind.Info:
            case ModSettingsItemKind.Link:
            case ModSettingsItemKind.Admin:
                return Contains(item.Label, q) || Contains(item.Options.Help, q);
            default:
                return false;
        }
    }

    private static bool Contains(string? text, string query) =>
        !string.IsNullOrEmpty(text) && text!.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The control for one row. The edit path decides first — a row nothing may write is a
    /// read-only line and an admin-editable synced row keeps its request flow — then an explicit
    /// presentation hint, then the value type and its bounds: a finite list (an enum, an
    /// <c>AcceptableValueList</c>, presentation choices) is a dropdown; a number with both bounds is
    /// a slider with an exact field; any other number is a stepper field.
    /// </summary>
    internal static SettingControl ControlFor(SettingsRow row)
    {
        if (row == null) return SettingControl.ReadOnly;
        if (row.Kind == SettingsEditorKind.Action) return SettingControl.Action;
        if (row.Kind == SettingsEditorKind.Keybind) return SettingControl.Keybind;
        var path = row.EditPath;
        if (path == SettingEditPath.ServerRequest) return SettingControl.ServerRequest;
        if (path == SettingEditPath.None || row.Kind == SettingsEditorKind.ReadOnly) return SettingControl.ReadOnly;

        var hint = row.EditorHint;
        bool explicitHint = row.HasPresentationHint;
        switch (row.Kind)
        {
            case SettingsEditorKind.Bool:
                return SettingControl.Toggle;
            case SettingsEditorKind.Enum:
                // A [Flags] enum holds a combination; a one-of selector cannot draw it. Stage 2: a
                // multi-select of its single-bit values, or text when it has more than the list shows.
                if (IsFlags(row.ValueType))
                {
                    int flags = FlagOptions(row.ValueType).Count;
                    return flags > 0 && flags <= MaxAutoMultiSelectOptions ? SettingControl.MultiSelect : SettingControl.Text;
                }

                return SettingControl.Dropdown;
            case SettingsEditorKind.Int:
            case SettingsEditorKind.Float:
            {
                if (ChoicesFor(row).Count > 0 && (!explicitHint || hint == SettingsEditorHint.Dropdown))
                    return SettingControl.Dropdown;
                var spec = NumberFor(row);
                if (explicitHint && hint == SettingsEditorHint.Numeric) return SettingControl.Number;
                if (explicitHint && hint == SettingsEditorHint.Slider) return spec.Bounded ? SettingControl.Slider : SettingControl.Number;
                return spec.Bounded ? SettingControl.Slider : SettingControl.Number;
            }
            case SettingsEditorKind.String:
            {
                if (explicitHint && hint == SettingsEditorHint.Color) return SettingControl.Color;
                if (ChoicesFor(row).Count > 0 && (!explicitHint || hint == SettingsEditorHint.Dropdown))
                    return SettingControl.Dropdown;
                return SettingControl.Text;
            }
            default:
                return SettingControl.Text;
        }
    }

    internal static string NameOf(SettingControl control) => control switch
    {
        SettingControl.Toggle => "toggle",
        SettingControl.Text => "text",
        SettingControl.Number => "number",
        SettingControl.Slider => "slider",
        SettingControl.Dropdown => "dropdown",
        SettingControl.Color => "color",
        SettingControl.Keybind => "keybind",
        SettingControl.Action => "action",
        SettingControl.ServerRequest => "server-request",
        SettingControl.Radio => "radio",
        SettingControl.MultiSelect => "multi-select",
        SettingControl.TextArea => "textarea",
        SettingControl.Segmented => "segmented",
        SettingControl.DateTime => "datetime",
        _ => "readonly",
    };

    private static bool IsFlags(Type? type)
    {
        if (type == null || !type.IsEnum) return false;
        try { return type.IsDefined(typeof(FlagsAttribute), inherit: false); }
        catch { return false; }
    }

    /// <summary>
    /// The finite values a row accepts, with their labels: an enum's names, an
    /// <c>AcceptableValueList</c>, or presentation <see cref="SettingPresentation.Choices"/>.
    /// Empty for a row with no finite set.
    /// </summary>
    internal static IReadOnlyList<(string Value, string Label)> ChoicesFor(SettingsRow row)
    {
        var result = new List<(string, string)>();
        if (row == null) return result;
        SettingDescriptor descriptor;
        try { descriptor = row.ToDescriptor(); }
        catch { return result; }
        var values = descriptor.AllowedValues ?? Array.Empty<string>();
        var labels = descriptor.ChoiceLabels ?? values;
        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i] ?? "";
            string label = i < labels.Length && !string.IsNullOrEmpty(labels[i]) ? labels[i] : value;
            result.Add((value, label));
        }

        return result;
    }

    /// <summary>
    /// OPTIONHOVER1: each choice's explanation, index-aligned with <see cref="ChoicesFor"/> (empty strings where the author
    /// gave none, or when the row has no <see cref="SettingPresentation.ChoiceDescriptions"/>).
    /// </summary>
    internal static IReadOnlyList<string> ChoiceDescriptionsFor(SettingsRow row)
    {
        var choices = ChoicesFor(row);
        var result = new string[choices.Count];
        string[]? given = row?.ChoiceDescriptions;
        for (int i = 0; i < result.Length; i++)
            result[i] = given != null && i < given.Length ? (given[i] ?? "").Trim() : "";
        return result;
    }

    /// <summary>TEXTAREA1: a single-line text setting's field holds this many characters unless the author sets a cap.</summary>
    internal const int DefaultTextMaxLength = 256;

    /// <summary>TEXTAREA1: a text area's field holds this many characters unless the author sets a cap.</summary>
    internal const int DefaultTextAreaMaxLength = 4096;

    /// <summary>
    /// TEXTAREA1: the character limit for a text setting's field. The author's cap
    /// (<c>.MaxLength(n)</c> or <c>SettingPresentation.MaxLength</c>) when set, otherwise
    /// <see cref="DefaultTextMaxLength"/> / <see cref="DefaultTextAreaMaxLength"/>; never below the
    /// stored value's length, because TMP cuts a longer value on assignment and the next focus loss
    /// would then save the cut text.
    /// </summary>
    internal static int TextFieldLimit(int? authorMax, bool multiline, int currentLength)
    {
        int cap = authorMax is int max && max > 0 ? max : multiline ? DefaultTextAreaMaxLength : DefaultTextMaxLength;
        return Math.Max(cap, Math.Max(0, currentLength));
    }

    /// <summary>
    /// Bounds, step, decimals and units of a numeric row. The step is the presentation's when it
    /// declares one; otherwise 1 for an integer, and for a decimal the finer of a "nice" hundredth of
    /// a declared range and the finest decimal the stored, default and bound values use — so the
    /// value the player already has is always on the grid and is never shown rounded.
    /// </summary>
    internal static SettingNumberSpec NumberFor(SettingsRow row)
    {
        bool integral = row?.Kind == SettingsEditorKind.Int;
        SettingDescriptor? descriptor = null;
        try { descriptor = row?.ToDescriptor(); }
        catch { descriptor = null; }

        bool hasMin = TryNumber(descriptor?.Min, out double min);
        bool hasMax = TryNumber(descriptor?.Max, out double max);
        if (hasMin && hasMax && max < min) (min, max) = (max, min);
        double typeMin = TypeMin(row?.ValueType);
        double typeMax = TypeMax(row?.ValueType);
        double lo = hasMin ? min : typeMin;
        double hi = hasMax ? max : typeMax;
        bool bounded = hasMin && hasMax && hi > lo;

        double step;
        int decimals;
        if (TryNumber(descriptor?.Step, out double declared) && declared > 0)
        {
            step = integral ? Math.Max(1, Math.Round(declared)) : declared;
            decimals = integral ? 0 : DecimalsOf(descriptor!.Step!);
        }
        else if (integral)
        {
            step = 1;
            decimals = 0;
        }
        else
        {
            int d = 0;
            d = Math.Max(d, DecimalsOf(descriptor?.CurrentValue));
            d = Math.Max(d, DecimalsOf(descriptor?.DefaultValue));
            if (hasMin) d = Math.Max(d, DecimalsOf(descriptor?.Min));
            if (hasMax) d = Math.Max(d, DecimalsOf(descriptor?.Max));
            d = Math.Min(d, MaxDecimals);
            double valueStep = Math.Pow(10, -d);
            step = valueStep;
            if (bounded)
            {
                double nice = Math.Pow(10, Math.Floor(Math.Log10((hi - lo) / 100.0)));
                if (nice > 0 && nice < step) step = nice;
            }
            else if (d == 0)
            {
                // An unbounded decimal stored as a whole number still steps in tenths.
                step = 0.1;
            }

            decimals = Math.Min(MaxDecimals, DecimalsOf(step.ToString("0.##########", CultureInfo.InvariantCulture)));
            decimals = Math.Max(decimals, d);
        }

        return new SettingNumberSpec(lo, hi, step, decimals, descriptor?.Units ?? "", bounded, integral);
    }

    /// <summary>
    /// The type's own range when nothing declares one (review finding 6): a number field never
    /// clamps a stored value its type can hold. <c>long</c> / <c>ulong</c> beyond 2^53 lose
    /// precision in the double the field edits.
    /// </summary>
    private static double TypeMin(Type? type)
    {
        if (type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong)) return 0;
        if (type == typeof(sbyte)) return sbyte.MinValue;
        if (type == typeof(short)) return short.MinValue;
        if (type == typeof(int)) return int.MinValue;
        if (type == typeof(long)) return long.MinValue;
        if (type == typeof(float)) return float.MinValue;
        if (type == typeof(decimal)) return (double)decimal.MinValue;
        return double.MinValue;
    }

    private static double TypeMax(Type? type)
    {
        if (type == typeof(byte)) return byte.MaxValue;
        if (type == typeof(sbyte)) return sbyte.MaxValue;
        if (type == typeof(short)) return short.MaxValue;
        if (type == typeof(ushort)) return ushort.MaxValue;
        if (type == typeof(int)) return int.MaxValue;
        if (type == typeof(uint)) return uint.MaxValue;
        // (double)long.MaxValue rounds up to 2^63, which no longer parses as a long; use the
        // largest double below it (likewise for ulong) so a stepper at the top still writes.
        if (type == typeof(long)) return 9223372036854774784d;
        if (type == typeof(ulong)) return 18446744073709549568d;
        if (type == typeof(float)) return float.MaxValue;
        if (type == typeof(decimal)) return (double)decimal.MaxValue;
        return double.MaxValue;
    }

    private static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Decimals an invariant number actually carries (<c>1.25</c> → 2, <c>1E-05</c> → 5), read from
    /// its value: the fewest decimals that reproduce it to single precision. A widened float's noise
    /// (<c>0.30000001192092896</c>) therefore counts as 1, not as the 6 its digits would say, so it
    /// cannot collapse a row's step on the next build (review finding 1).
    /// </summary>
    internal static int DecimalsOf(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.Length == 0) return 0;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            || double.IsNaN(v) || double.IsInfinity(v))
            return 0;
        double tolerance = 1e-7 * Math.Max(1.0, Math.Abs(v));
        for (int d = 0; d < MaxDecimals; d++)
        {
            if (Math.Abs(Math.Round(v, d, MidpointRounding.AwayFromZero) - v) <= tolerance) return d;
        }

        return MaxDecimals;
    }

    // ---- defaults ---------------------------------------------------------------------------

    /// <summary>
    /// True when the stored value differs from the default. Typed rows compare parsed values, so
    /// <c>1.50</c> and <c>1.5</c> are the same; a value that does not parse compares as text.
    /// </summary>
    internal static bool IsModified(SettingsRow row)
    {
        if (row == null || row.Kind == SettingsEditorKind.Action) return false;
        string current, fallback;
        try
        {
            current = row.Read();
            fallback = row.DefaultValue;
        }
        catch { return false; }

        if (row.Kind == SettingsEditorKind.Keybind)
            return !string.Equals(current, fallback, StringComparison.Ordinal);
        if (row.ValueType != null && row.ValueType != typeof(string)
            && SettingsEditors.TryParse(current, row.ValueType, out var a, out _)
            && SettingsEditors.TryParse(fallback, row.ValueType, out var b, out _))
            return !Equals(a, b);
        return !string.Equals(current, fallback, StringComparison.Ordinal);
    }

    /// <summary>A reset is offered only where the page can write: a changed row on the local config path, or a changed keybind.</summary>
    internal static bool CanReset(SettingsRow row)
    {
        if (row == null) return false;
        if (row.Kind == SettingsEditorKind.Keybind) return IsModified(row);
        if (row.Kind == SettingsEditorKind.Action) return false;
        return row.EditPath == SettingEditPath.LocalConfig && IsModified(row);
    }

    /// <summary>Rows that can ever carry a reset arrow: a local write path, or a keybind.</summary>
    internal static bool CanEverReset(SettingsRow row) =>
        row != null && (row.Kind == SettingsEditorKind.Keybind
            || (row.Kind != SettingsEditorKind.Action && row.EditPath == SettingEditPath.LocalConfig));

    /// <summary>
    /// The rows "Reset all to defaults" acts on: <see cref="CanReset"/>, less any row whose entry
    /// lives in another plugin's config file (review finding 5) — a mod's page may show such an
    /// entry, and even edit it when <c>[Settings] EditOtherModsSettings</c> allows, but a page-wide
    /// reset must never rewrite another mod's settings.
    /// </summary>
    internal static bool InResetAll(SettingsRow row) => row != null && !row.OwnedElsewhere && CanReset(row);

    /// <summary>
    /// Put one row back to its default through the same validation and permission path as any other
    /// write: a registered binding's own Reset, a keybind's rebind, else an ordinary submit.
    /// </summary>
    internal static SettingWriteResult Reset(SettingsRow row)
    {
        if (row == null) return SettingWriteResult.Refused("", "no setting");
        if (!CanReset(row)) return SettingWriteResult.Idle(row.Read());
        if (row.Kind == SettingsEditorKind.Keybind)
        {
            var chord = row.DefaultChord;
            // KEYS2: a default that is now reserved or used by something else is refused with the reason,
            // never stored as a new conflict.
            string? why = null;
            bool ok = chord.HasValue && Keybinds.TryRebind(row.PluginId, row.Key, chord.Value, Keybinds.RebindCheck.Reset, out why);
            return ok
                ? SettingWriteResult.Accepted(row.Read())
                : SettingWriteResult.Refused(row.Read(), why ?? "this keybind is no longer registered");
        }

        if (SettingsCatalog.TryGetBinding(row.RowId, out var binding) && binding != null)
            return SettingsCatalog.Reset(row.RowId);
        return SettingsCatalog.Submit(row, row.DefaultValue);
    }

    /// <summary>The rows <see cref="Reset"/> would change: what "Reset all to defaults" acts on.</summary>
    internal static List<SettingsRow> Resettable(IReadOnlyList<ModSettingsSection>? sections)
    {
        var result = new List<SettingsRow>();
        if (sections == null) return result;
        for (int i = 0; i < sections.Count; i++)
            for (int j = 0; j < sections[i].Rows.Count; j++)
                if (InResetAll(sections[i].Rows[j])) result.Add(sections[i].Rows[j]);
        return result;
    }

    /// <summary>
    /// The rows of a registered group. Synced entries go through the synced probe and presentation
    /// comes from a registered binding, as in <see cref="SettingsCatalog.Rows"/>. An entry whose
    /// config file is not this plugin's own (<see cref="SettingsCatalog.IsOwnedElsewhere"/>) is
    /// marked as belonging elsewhere and, like the catalog's foreign rows, is read-only unless
    /// <see cref="SettingsCatalog.AllowForeignEdits"/> is set (review finding 5).
    /// </summary>
    internal static List<SettingsRow> RowsOf(string pluginId, IReadOnlyList<ConfigEntryBase>? entries)
    {
        var rows = new List<SettingsRow>();
        if (entries == null) return rows;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry == null) continue;
            rows.Add(RowOf(pluginId, entry, null));
        }

        return rows;
    }

    /// <summary>
    /// One entry's row, as <see cref="RowsOf"/> builds it, with a builder's presentation laid over the
    /// registered binding's: a field the builder sets wins, a field it leaves null keeps the binding's.
    /// </summary>
    internal static SettingsRow RowOf(string pluginId, ConfigEntryBase entry, SettingPresentation? builder)
    {
        bool elsewhere = SettingsCatalog.IsOwnedElsewhere(pluginId, entry);
        var registered = SettingsCatalog.PresentationFor(pluginId, entry.Definition.Section, entry.Definition.Key);
        return SettingsRow.ForEntry(
            pluginId,
            entry,
            SettingsCatalog.IsSynced(entry),
            foreign: elsewhere && !SettingsCatalog.AllowForeignEdits,
            presentation: Merge(registered, builder))
            .MarkOwnedElsewhere(elsewhere);
    }

    internal static SettingPresentation? Merge(SettingPresentation? registered, SettingPresentation? builder)
    {
        if (builder == null) return registered;
        if (registered == null) return builder;
        return new SettingPresentation
        {
            EditorHint = builder.EditorHint ?? registered.EditorHint,
            CommitPolicy = builder.CommitPolicy ?? registered.CommitPolicy,
            Label = builder.Label ?? registered.Label,
            Help = builder.Help ?? registered.Help,
            Group = builder.Group ?? registered.Group,
            Order = builder.Order != 0 ? builder.Order : registered.Order,
            Units = builder.Units ?? registered.Units,
            Step = builder.Step ?? registered.Step,
            Min = builder.Min ?? registered.Min,
            Max = builder.Max ?? registered.Max,
            MaxLength = builder.MaxLength ?? registered.MaxLength,
            Choices = builder.Choices ?? registered.Choices,
            ChoiceLabels = builder.ChoiceLabels ?? registered.ChoiceLabels,
            ChoiceDescriptions = builder.ChoiceDescriptions ?? registered.ChoiceDescriptions,
            DefaultValue = builder.DefaultValue ?? registered.DefaultValue,
            AllowServerEdits = builder.AllowServerEdits || registered.AllowServerEdits,
            HasAlpha = builder.HasAlpha || registered.HasAlpha,
        };
    }

    // ---- stage 2: sections from a settings page ------------------------------------------------

    /// <summary>
    /// SETTINGS1 stage 2: the sections, in order: the mod's settings page sections
    /// (<c>ctx.Settings.Page</c>), then every <c>.Settings(title, …)</c> group, then General, Keys and
    /// Actions. <b>The builder wins</b>: an entry the page places is claimed first, so a group or the
    /// catalog walk that names it again is skipped. Within the page an entry placed twice keeps its
    /// first place. Empty sections are dropped; a page section with only buttons, info lines or
    /// dividers is kept.
    /// </summary>
    internal static List<ModSettingsSection> Build(
        IReadOnlyList<ModSettingsSection>? pageSections,
        IReadOnlyList<(string Title, IReadOnlyList<SettingsRow> Rows)>? groups,
        IReadOnlyList<SettingsRow>? pluginRows)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModSettingsSection>();
        if (pageSections != null)
        {
            for (int i = 0; i < pageSections.Count; i++)
            {
                var section = pageSections[i];
                if (section == null) continue;
                var items = new List<ModSettingsItem>(section.Items.Count);
                for (int j = 0; j < section.Items.Count; j++)
                {
                    var item = section.Items[j];
                    if (item == null) continue;
                    if (item.Kind == ModSettingsItemKind.Setting && (item.Row == null || !claimed.Add(item.Row.RowId))) continue;
                    // A published setting the page placed by id (an item list, a PerPlayer setting): its cfg row, if
                    // this side has one, is not drawn again in the automatic sections.
                    if (item.Kind == ModSettingsItemKind.Admin && item.Admin is { Manifest: true } && !claimed.Add(item.Id)) continue;
                    if (item.Options.ClaimOnly)
                    {
                        if (item.Options.ScopedRowId is { } claim) claimed.Add(claim);
                        continue;
                    }
                    items.Add(item);
                }

                bool meaningful = false;
                for (int j = 0; j < items.Count && !meaningful; j++) meaningful = items[j].Kind != ModSettingsItemKind.Divider;
                if (meaningful) result.Add(new ModSettingsSection(section.Title, ModSettingsSectionKind.Page, items));
            }
        }

        // Groups and the catalog skip what the page claimed.
        var filteredGroups = new List<(string Title, IReadOnlyList<SettingsRow> Rows)>();
        if (groups != null)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                var rows = new List<SettingsRow>();
                var source = groups[i].Rows;
                if (source != null)
                    for (int j = 0; j < source.Count; j++)
                        if (source[j] != null && !claimed.Contains(source[j].RowId)) rows.Add(source[j]);
                filteredGroups.Add((groups[i].Title, rows));
            }
        }

        var filteredRows = new List<SettingsRow>();
        if (pluginRows != null)
            for (int i = 0; i < pluginRows.Count; i++)
                if (pluginRows[i] != null && !claimed.Contains(pluginRows[i].RowId)) filteredRows.Add(pluginRows[i]);

        result.AddRange(Build(filteredGroups, filteredRows));
        return result;
    }

    /// <summary>Items that count for the search threshold and the shown count: everything but dividers.</summary>
    internal static int ItemCount(IReadOnlyList<ModSettingsSection>? sections)
    {
        int n = 0;
        if (sections == null) return 0;
        for (int i = 0; i < sections.Count; i++)
            for (int j = 0; j < sections[i].Items.Count; j++)
                if (sections[i].Items[j].Kind != ModSettingsItemKind.Divider) n++;
        return n;
    }

    // ---- stage 2: which control, drawn how --------------------------------------------------------

    /// <summary>Most single-bit values a <c>[Flags]</c> enum may have to get a multi-select automatically.</summary>
    internal const int MaxAutoMultiSelectOptions = 8;

    /// <summary>
    /// A radio or multi-select drawn as a list grows 30 px per option; past this many options a radio
    /// is a dropdown and a multi-select is a text field, so no list row reaches the panel's row cap.
    /// </summary>
    internal const int MaxListOptions = 12;

    /// <summary>
    /// SETTINGS1 stage 2: the control for a row with a builder's options. The edit path still decides
    /// first (a read-only or admin-request row stays one, whatever the builder asked); an asked control
    /// the value cannot take (a radio on a free number, a text area on an int) falls back to
    /// <see cref="ControlFor(SettingsRow)"/>.
    /// </summary>
    internal static SettingControl ControlFor(SettingsRow row, ModSettingOptions? options)
    {
        var automatic = ControlFor(row);
        if (options?.Control is not { } asked || row == null) return automatic;
        if (automatic is SettingControl.ReadOnly or SettingControl.ServerRequest
            or SettingControl.Action or SettingControl.Keybind)
            return automatic;
        bool numeric = row.Kind is SettingsEditorKind.Int or SettingsEditorKind.Float;
        bool text = row.Kind == SettingsEditorKind.String;
        switch (asked)
        {
            case SettingControl.Toggle:
                return row.Kind == SettingsEditorKind.Bool ? SettingControl.Toggle : automatic;
            case SettingControl.Radio:
            {
                int n = ChoicesFor(row).Count;
                if (n == 0) return automatic;
                return n > MaxListOptions ? SettingControl.Dropdown : SettingControl.Radio;
            }
            case SettingControl.Dropdown:
                return ChoicesFor(row).Count > 0 ? SettingControl.Dropdown : automatic;
            case SettingControl.MultiSelect:
            {
                int n = MultiChoicesFor(row).Count;
                if (n == 0) return automatic;
                return n > MaxListOptions ? (text ? SettingControl.Text : automatic) : SettingControl.MultiSelect;
            }
            case SettingControl.TextArea:
                return text ? SettingControl.TextArea : automatic;
            case SettingControl.Text:
                return text || row.Kind == SettingsEditorKind.Enum ? SettingControl.Text : automatic;
            case SettingControl.Color:
                return text ? SettingControl.Color : automatic;
            case SettingControl.Slider:
                return numeric ? (NumberFor(row).Bounded ? SettingControl.Slider : SettingControl.Number) : automatic;
            case SettingControl.Number:
                return numeric ? SettingControl.Number : automatic;
            case SettingControl.DateTime:
                return text ? SettingControl.DateTime : automatic;
            default:
                return automatic;
        }
    }

    /// <summary>
    /// JOINFLOW1: a string setting the builder asked to draw as the date-time field (<c>.DateTime(...)</c>).
    /// The edit path still picks the control (an admin's row is the request path, a non-admin's is
    /// read-only); this says the value is an ISO 8601 UTC time, so the admin's control is the picker and
    /// a read-only value is shown as a wall-clock time.
    /// </summary>
    internal static bool IsDateTimeRow(SettingsRow? row, ModSettingOptions? options) =>
        // ValueType, not Kind: a server-owned row's Kind is read-only whatever its type.
        row != null && options?.Control == SettingControl.DateTime && row.ValueType == typeof(string);

    /// <summary>JOINFLOW1: the precision a date-time row's picker opens with (its <c>seconds</c> function, read now).</summary>
    internal static DateTimePickerPrecision DateTimePrecision(ModSettingOptions? options)
    {
        var seconds = options?.DateTimeSeconds;
        if (seconds == null) return DateTimePickerPrecision.Minute;
        try { return seconds() ? DateTimePickerPrecision.Second : DateTimePickerPrecision.Minute; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings date-time seconds function threw: " + e.Message);
            return DateTimePickerPrecision.Minute;
        }
    }

    /// <summary>
    /// JOINFLOW1: the zone a date-time row is shown in: its <c>zone</c> function's answer, read now, else
    /// <paramref name="fallback"/> (the player's own zone for a personal row, UTC for a server-wide one).
    /// </summary>
    internal static TimeZoneInfo DateTimeZone(ModSettingOptions? options, TimeZoneInfo fallback)
    {
        var zone = options?.DateTimeZone;
        if (zone == null) return fallback;
        try { return zone() ?? fallback; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings date-time zone function threw: " + e.Message);
            return fallback;
        }
    }

    /// <summary>
    /// TEXTLIMIT1: a text setting the builder asked to draw as a text area (<c>.Text(multiline: true)</c>).
    /// The edit path still picks the control (an admin's row is the request path, a non-admin's is
    /// read-only); this says the value is multi-line text, so the admin's field is the real text area
    /// and the read-only value opens in the expanded reader.
    /// </summary>
    internal static bool IsTextAreaRow(SettingsRow? row, ModSettingOptions? options) =>
        // ValueType, not Kind: a server-owned row's Kind is read-only whatever its type.
        row != null && options?.Control == SettingControl.TextArea && row.ValueType == typeof(string);

    /// <summary>
    /// SETTINGS1 stage 2: what the page draws for a control — an automatic dropdown whose two to four
    /// options are short is a segmented picker (<see cref="UiOptionsRules.FitsSegmented"/>); a dropdown
    /// the builder asked for by name stays a selector.
    /// </summary>
    internal static SettingControl DrawnControl(SettingsRow row, ModSettingOptions? options)
    {
        var control = ControlFor(row, options);
        if (control != SettingControl.Dropdown || options?.Control is SettingControl.Dropdown or SettingControl.Radio) return control;
        var choices = ChoicesFor(row);
        var labels = new List<string>(choices.Count);
        for (int i = 0; i < choices.Count; i++) labels.Add(choices[i].Label);
        return UiOptionsRules.FitsSegmented(labels) ? SettingControl.Segmented : control;
    }

    /// <summary>
    /// An enum value's bits as an unsigned number of the enum's own width, without overflow: a flag
    /// at <c>1 &lt;&lt; 31</c> of an <c>int</c> enum is <c>0x80000000</c>, not a throw.
    /// </summary>
    internal static ulong EnumBits(object? value)
    {
        if (value == null) return 0;
        switch (Type.GetTypeCode(value.GetType().IsEnum ? Enum.GetUnderlyingType(value.GetType()) : value.GetType()))
        {
            case TypeCode.SByte: return unchecked((byte)Convert.ToSByte(value, CultureInfo.InvariantCulture));
            case TypeCode.Int16: return unchecked((ushort)Convert.ToInt16(value, CultureInfo.InvariantCulture));
            case TypeCode.Int32: return unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture));
            case TypeCode.Int64: return unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
            default: return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The single-bit named values of a <c>[Flags]</c> enum, in declaration order.</summary>
    internal static IReadOnlyList<string> FlagOptions(Type? type)
    {
        var names = new List<string>();
        if (type == null || !type.IsEnum) return names;
        try
        {
            foreach (var name in Enum.GetNames(type))
            {
                ulong bits = EnumBits(Enum.Parse(type, name));
                if (bits != 0 && (bits & (bits - 1)) == 0) names.Add(name);
            }
        }
        catch { names.Clear(); }
        return names;
    }

    /// <summary>
    /// The options of a multi-select row: a <c>[Flags]</c> enum's single-bit values, or a string
    /// row's finite list (presentation choices — a builder's <c>choices:</c>). Empty when neither,
    /// and for a string entry with an <c>AcceptableValueList</c>: that list accepts one value, so
    /// every multi-value write would be refused.
    /// </summary>
    internal static IReadOnlyList<(string Value, string Label)> MultiChoicesFor(SettingsRow row)
    {
        var result = new List<(string, string)>();
        if (row == null) return result;
        if (IsFlags(row.ValueType))
        {
            foreach (var name in FlagOptions(row.ValueType)) result.Add((name, name));
            return result;
        }

        if (row.Kind != SettingsEditorKind.String || HasAcceptableList(row)) return result;
        return ChoicesFor(row);
    }

    /// <summary>True when the entry carries an <c>AcceptableValueList</c> (its bounds read <c>one of: …</c>).</summary>
    internal static bool HasAcceptableList(SettingsRow row)
    {
        string bounds;
        try { bounds = row?.Bounds ?? ""; }
        catch { bounds = ""; }
        return bounds.StartsWith("one of: ", StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>[Flags]</c> value from its stored text: names (<c>Combat, Loot</c>) or a number (a value
    /// with bits no name covers is stored as one, e.g. <c>9</c>). False when neither parses.
    /// </summary>
    internal static bool TryFlagBits(Type type, string? stored, out ulong bits)
    {
        bits = 0;
        string s = (stored ?? "").Trim();
        if (s.Length == 0) return false;
        if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out bits)) return true;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
        {
            bits = EnumBits(Enum.ToObject(type, signed));
            return true;
        }

        if (!SettingsEditors.TryParse(s, type, out var parsed, out _) || parsed == null) return false;
        bits = EnumBits(parsed);
        return true;
    }

    private static ulong NamedBits(Type type)
    {
        ulong mask = 0;
        foreach (var name in FlagOptions(type)) mask |= EnumBits(Enum.Parse(type, name));
        return mask;
    }

    /// <summary>The option values <paramref name="stored"/> selects, in their canonical spelling (case-insensitive for a list).</summary>
    internal static List<string> SelectedFor(SettingsRow row, string? stored)
    {
        var selected = new List<string>();
        if (row == null) return selected;
        if (IsFlags(row.ValueType))
        {
            try
            {
                if (!TryFlagBits(row.ValueType!, stored, out ulong value)) return selected;
                foreach (var name in FlagOptions(row.ValueType))
                {
                    ulong bit = EnumBits(Enum.Parse(row.ValueType!, name));
                    if ((value & bit) == bit) selected.Add(name);
                }
            }
            catch { selected.Clear(); }
            return selected;
        }

        var options = MultiChoicesFor(row);
        foreach (var part in (stored ?? "").Split(','))
        {
            string t = part.Trim();
            if (t.Length == 0) continue;
            for (int i = 0; i < options.Count; i++)
            {
                if (!string.Equals(options[i].Value, t, StringComparison.OrdinalIgnoreCase)) continue;
                t = options[i].Value;
                break;
            }

            if (!selected.Contains(t)) selected.Add(t);
        }

        return selected;
    }

    /// <summary>
    /// The text a selection is written as.
    /// <list type="bullet">
    /// <item><description>A <c>[Flags]</c> enum: the selected names joined with <c>", "</c>; bits of
    /// <paramref name="stored"/> that no single-bit name covers are kept, and then the value is written
    /// as its number (the only form that carries them). The empty set is the zero-valued name, and is
    /// refused when the enum names none.</description></item>
    /// <item><description>A string list: the selected options in option order and canonical spelling,
    /// then every token of <paramref name="stored"/> that is not an option, unchanged, so a value the
    /// list does not know is never dropped by a click on another.</description></item>
    /// </list>
    /// </summary>
    internal static bool TryFormatSelection(
        SettingsRow row, IReadOnlyList<string>? selected, out string text, out string error, string? stored = null)
    {
        text = "";
        error = "";
        var options = MultiChoicesFor(row);
        var set = new HashSet<string>(selected ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var chosen = new List<string>();
        for (int i = 0; i < options.Count; i++) if (set.Contains(options[i].Value)) chosen.Add(options[i].Value);

        if (IsFlags(row?.ValueType))
        {
            var type = row!.ValueType!;
            try
            {
                ulong value = 0;
                foreach (var name in chosen) value |= EnumBits(Enum.Parse(type, name));
                if (TryFlagBits(type, stored, out ulong before)) value |= before & ~NamedBits(type);
                bool unnamed = (value & ~NamedBits(type)) != 0;
                if (unnamed)
                {
                    text = value.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (chosen.Count > 0)
                {
                    text = string.Join(", ", chosen);
                    return true;
                }

                foreach (var name in Enum.GetNames(type))
                {
                    if (EnumBits(Enum.Parse(type, name)) != 0) continue;
                    text = name;
                    return true;
                }
            }
            catch { /* fall through to the refusal */ }

            error = "choose at least one";
            return false;
        }

        foreach (var part in (stored ?? "").Split(','))
        {
            string t = part.Trim();
            if (t.Length == 0) continue;
            bool known = false;
            for (int i = 0; i < options.Count && !known; i++)
                known = string.Equals(options[i].Value, t, StringComparison.OrdinalIgnoreCase);
            if (!known && !chosen.Contains(t)) chosen.Add(t);
        }

        text = string.Join(", ", chosen);
        return true;
    }

    // ---- stage 2: display names ------------------------------------------------------------------

    /// <summary>
    /// SETTINGS1 stage 2: the name a setting is shown under, from its cfg key: camelCase, PascalCase,
    /// underscores, hyphens and dots become words in sentence case (<c>ShowHints</c> → <c>Show hints</c>,
    /// <c>spawn_multiplier</c> → <c>Spawn multiplier</c>). An all-caps run of two or more letters keeps
    /// its case (<c>HTTPPort</c> → <c>HTTP port</c>), and so does every word of <see cref="Acronyms"/>
    /// whatever the key's case (<c>UiDemoGreeting</c> → <c>UI demo greeting</c>, <c>PvpTweak</c> →
    /// <c>PvP tweak</c>, <c>IPv6Address</c> → <c>IPv6 address</c>). A number starts a word and keeps the
    /// capitals that follow it (<c>Enable3DAudio</c> → <c>Enable 3D audio</c>, <c>Level2Cap</c> →
    /// <c>Level 2 cap</c>).
    /// </summary>
    internal static string DisplayName(string? key)
    {
        string s = (key ?? "").Trim();
        if (s.Length == 0) return "";
        var words = new List<string>();
        var fixedWords = new HashSet<int>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }

        for (int i = 0; i < s.Length; i++)
        {
            // A listed mixed-case word (PvP, IPv6) is one word however the key cases it.
            string? special = SpecialAt(s, i);
            if (special != null)
            {
                Flush();
                fixedWords.Add(words.Count);
                words.Add(special);
                i += special.Length - 1;
                continue;
            }

            char c = s[i];
            if (c == '_' || c == '-' || c == '.' || c == ' ' || c == '/')
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                char prev = current[current.Length - 1];
                bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                bool boundary =
                    (char.IsLower(prev) && char.IsUpper(c))
                    || (char.IsLetter(prev) && char.IsDigit(c))
                    // "2Cap" splits (a word starts), "3DAudio" keeps "3D" together.
                    || (char.IsDigit(prev) && char.IsUpper(c) && nextLower)
                    || (char.IsDigit(prev) && char.IsLower(c))
                    // The end of an acronym: "HTTPPort" splits before the P that starts "Port".
                    || (char.IsUpper(prev) && char.IsUpper(c) && nextLower);
                if (boundary) Flush();
            }

            current.Append(c);
        }

        Flush();
        for (int i = 0; i < words.Count; i++)
        {
            if (fixedWords.Contains(i)) continue;
            string w = words[i];
            if (Acronyms.TryGetValue(w, out var known))
            {
                words[i] = known;
                continue;
            }

            bool acronym = w.Length >= 2 && IsAllUpper(w);
            if (acronym) continue;
            w = w.ToLowerInvariant();
            if (i == 0 && w.Length > 0) w = char.ToUpperInvariant(w[0]) + w.Substring(1);
            words[i] = w;
        }

        return string.Join(" ", words);
    }

    /// <summary>
    /// Words a display name keeps in their own case wherever they appear, whatever the key's case
    /// (<c>UiDemoGreeting</c> → <c>UI demo greeting</c>, <c>PvpTweak</c> → <c>PvP tweak</c>).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Acronyms = BuildAcronyms(
        "UI", "HUD", "PvP", "PvE", "ID", "FPS", "XP", "HP", "MP", "NPC", "VFX", "SFX", "AI", "API", "URL",
        "DPS", "AoE", "UTC", "GUID", "IPv4", "IPv6");

    /// <summary>Mixed-case words matched in the key before it is split (they would split at their inner capital or digit).</summary>
    private static readonly string[] SpecialWords = { "IPv4", "IPv6", "PvP", "PvE", "AoE" };

    private static string? SpecialAt(string s, int i)
    {
        foreach (var word in SpecialWords)
        {
            if (i + word.Length > s.Length) continue;
            if (!string.Equals(s.Substring(i, word.Length), word, StringComparison.OrdinalIgnoreCase)) continue;
            // Only at a word start, and only when the next character starts a new word.
            if (i > 0 && char.IsLetter(s[i - 1]) && !char.IsUpper(s[i])) continue;
            int end = i + word.Length;
            if (end < s.Length && char.IsLower(s[end])) continue;
            return word;
        }

        return null;
    }

    private static Dictionary<string, string> BuildAcronyms(params string[] words)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in words) map[w] = w;
        return map;
    }

    private static bool IsAllUpper(string w)
    {
        bool letter = false;
        for (int i = 0; i < w.Length; i++)
        {
            if (char.IsLetter(w[i]))
            {
                letter = true;
                if (!char.IsUpper(w[i])) return false;
            }
        }

        return letter;
    }

    /// <summary>
    /// The name a row is drawn under: a builder's label, else a registered presentation label
    /// (<see cref="SettingPresentation.Label"/>), else <see cref="DisplayName"/> of the cfg key. Never
    /// <c>[Section] Key</c>; that stays the readback's <c>label</c> and is searchable.
    /// </summary>
    internal static string DisplayNameOf(SettingsRow row, ModSettingOptions? options = null)
    {
        if (row == null) return "";
        if (!string.IsNullOrWhiteSpace(options?.Label)) return options!.Label!.Trim();
        if (row.HasPresentationLabel || row.Kind == SettingsEditorKind.Action) return row.Label;
        if (row.Kind == SettingsEditorKind.Keybind) return KeybindArbiter.KeybindDisplayName(row.PluginId, row.Key);
        return DisplayName(row.Key);
    }

    // ---- stage 2: common options -----------------------------------------------------------------

    /// <summary>True when two texts are the same value of the row's type (parsed; verbatim for strings).</summary>
    internal static bool SameValue(SettingsRow row, string? a, string? b)
    {
        if (row?.ValueType != null && row.ValueType != typeof(string)
            && SettingsEditors.TryParse(a, row.ValueType, out var x, out _)
            && SettingsEditors.TryParse(b, row.ValueType, out var y, out _))
            return Equals(x, y);
        return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    }

    /// <summary>A needs-restart row whose value differs from the value it had when the mod loaded.</summary>
    internal static bool IsPendingRestart(SettingsRow row, ModSettingOptions? options)
    {
        if (row == null || options == null || !options.NeedsRestart || options.LoadValue == null) return false;
        string now;
        try { now = row.Read(); }
        catch { return false; }
        return !SameValue(row, now, options.LoadValue);
    }

    /// <summary>
    /// SETTINGS1 stage 2: is this mod's server part known to run on the server this client is joined
    /// to, for graying a needs-server-part row. The same evidence the framework line reads
    /// (<see cref="ModFrameworkLine.DecideServerPart"/>): a pause means it does not; the server's
    /// handshake request naming the mod means it does. Unlike the line, the sides are not required
    /// here — the request is direct evidence — and <b>unknown grays</b>: only the request proves the
    /// part is there, so offline, before the handshake, and for a mod no server named, the row is
    /// grayed with its fallback.
    /// </summary>
    internal static ServerPartState ServerPartForSettings(bool paused, bool serverAskedAboutIt, bool knownAbsent = false)
    {
        if (paused) return ServerPartState.NotOnServer;
        if (serverAskedAboutIt) return ServerPartState.Running;
        // Stage 3: the joined server's full handshake request did not name the mod (a counterpart
        // mod that runs limited without it): the part is known to be missing, not merely unconfirmed.
        return knownAbsent ? ServerPartState.NotOnServer : ServerPartState.Unknown;
    }

    // Settings scopes step 4: the framework's standard words (SettingAvailability). Joined to a Progenitor
    // server, a part the handshake did not name reads the same as one known absent; offline, waiting and
    // a vanilla server have their own reasons, which the page takes from SettingAvailability.
    internal const string ServerPartMissingReason = SettingAvailability.NeedsServerPartReason;
    internal const string ServerPartUnknownReason = SettingAvailability.NeedsServerPartReason;

    /// <summary>The reason a needs-server-part row is grayed, or empty when the part runs.</summary>
    internal static string ServerPartReason(ServerPartState state) => state switch
    {
        ServerPartState.Running => "",
        ServerPartState.NotOnServer => ServerPartMissingReason,
        _ => ServerPartUnknownReason,
    };

    // ---- stage 2: the help line ------------------------------------------------------------------

    /// <summary>
    /// The note on a setting that is not at its default: "The default is 5." (user feedback 2026-10-07: "Changed from 0"
    /// read as the previous value).
    /// </summary>
    internal const string ChangedFromPrefix = "The default is ";
    internal const string PendingRestartMarker = "Pending restart";
    internal const string ScopeServerMarker = "Server-set";
    internal const string ScopeLocalMarker = "Local to this client";
    internal const string ReadOnlyAwaitingReason = "Set by the server. Join a server to see its value.";
    internal const string ReadOnlyLockedReason = "Set by the server.";
    internal const string ReadOnlyForeignReason = "Belongs to another mod; change it there.";
    internal const string ReadOnlyOtherReason = "Not editable here.";

    /// <summary>The plain-words reason a read-only row cannot be edited (docs/mod-settings.md).</summary>
    internal static string ReadOnlyReason(SettingsRow row)
    {
        if (row == null) return ReadOnlyOtherReason;
        if (row.IsSynced || row.IsSyncedNow)
            return ConfigSync.IsLocked(row.PluginId, row.Section, row.Key) ? ReadOnlyLockedReason : ReadOnlyAwaitingReason;
        if (row.IsForeign || row.OwnedElsewhere) return ReadOnlyForeignReason;
        return ReadOnlyOtherReason;
    }

    /// <summary>One sentence: trimmed, with a full stop added when it ends without punctuation.</summary>
    internal static string Sentence(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return "";
        char last = t[t.Length - 1];
        return last is '.' or '!' or '?' or '…' or ':' or ')' ? t : t + ".";
    }

    /// <summary>
    /// The help line under a row's name, in order: the help (the builder's, else the entry's
    /// description), the read-only or grayed reason and the fallback, the scope marker when one was
    /// declared, <c>The default is &lt;default&gt;.</c> while modified, <c>Pending restart.</c> while a
    /// needs-restart change waits, and a transient status (<c>Saved.</c> or a refusal).
    /// </summary>
    internal static string HelpText(
        SettingsRow row, ModSettingOptions? options, SettingControl control, string? why, string? status, bool scopeWhy = false)
    {
        var (help, notes) = HelpParts(row, options, control, why, status, scopeWhy);
        var parts = new List<string>(notes.Count + 1);
        if (help.Length > 0) parts.Add(help);
        parts.AddRange(notes);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// MENUKIT2: <see cref="HelpText"/> in two parts, for the description column: the help sentence
    /// (the builder's, else the entry's description) and the notes after it, in the same order.
    /// </summary>
    internal static (string Help, List<string> Notes) HelpParts(
        SettingsRow row, ModSettingOptions? options, SettingControl control, string? why, string? status, bool scopeWhy = false)
    {
        var parts = new List<string>(6);
        string helpText = !string.IsNullOrWhiteSpace(options?.Help) ? options!.Help! : row?.Description ?? "";
        string help = helpText.Trim().Length > 0 ? Sentence(helpText) : "";
        // Settings scopes: an admin-only or scope-grayed row's reason (`why`) already says why it cannot
        // be changed; the read-only reason would say it twice ("Set by the server. Join a server …").
        if (control == SettingControl.ReadOnly && row != null && !scopeWhy) parts.Add(Sentence(ReadOnlyReason(row)));
        if (!string.IsNullOrWhiteSpace(why)) parts.Add(Sentence(why));
        // A read-only or admin-request row already says the server owns it; say it once.
        if (options?.Scope is { } scope
            && !(scope == SettingScope.Server && control is SettingControl.ReadOnly or SettingControl.ServerRequest))
            parts.Add(Sentence(scope == SettingScope.Server ? ScopeServerMarker : ScopeLocalMarker));
        if (row != null && control != SettingControl.Action && IsModified(row))
        {
            string d;
            try { d = row.DefaultValue; }
            catch { d = ""; }
            parts.Add(ChangedFromPrefix + (d.Length == 0 ? "(empty)" : d) + ".");
        }

        if (row != null && IsPendingRestart(row, options)) parts.Add(PendingRestartMarker + ".");
        // KEYS1: a key in conflict says so on its own row with the winner rule's sentence: the loser's
        // "Not active: F8 is used by X, installed first. Pick another key.", the winner's "Also used by X
        // (not active there)." — the same words the Keybinds tab and the problems line use.
        if (row != null && row.Kind == SettingsEditorKind.Keybind)
        {
            string line = KeyConflictLineOf(row);
            if (line.Length > 0) parts.Add(line);
        }

        if (!string.IsNullOrWhiteSpace(status)) parts.Add(Sentence(status));
        return (help, parts);
    }

    /// <summary>Stage 3: the other mod a keybind row's chord clashes with, or empty (<see cref="ModProblems.KeyConflictOwner"/>).</summary>
    internal static string KeyConflictLineOf(SettingsRow row)
    {
        if (row == null || row.Kind != SettingsEditorKind.Keybind) return "";
        try { return ModProblems.KeyConflictLine(row.PluginId, row.Key); }
        catch { return ""; }
    }

    internal static string KeyConflictOf(SettingsRow row)
    {
        if (row == null || row.Kind != SettingsEditorKind.Keybind) return "";
        try { return ModProblems.KeyConflictOwner(row.PluginId, row.Key); }
        catch { return ""; }
    }
}
