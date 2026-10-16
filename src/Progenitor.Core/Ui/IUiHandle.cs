using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// One cloned vanilla widget. Core-owned so Unity never holds a plugin-implemented
/// <c>MonoBehaviour</c> or a collectible <c>UnityAction</c> (evidence 88). Dispose
/// releases the GameObject this handle created. Double-dispose is a no-op.
/// </summary>
public interface IUiHandle : IDisposable
{
    /// <summary>Visible label. TMP text; bypasses <c>LocalizedText</c> which would overwrite it.</summary>
    string Text { get; set; }

    /// <summary>GameObject active flag. Hidden widgets still exist until Dispose.</summary>
    bool Visible { get; set; }

    /// <summary>
    /// False on the server, when the module is skipped, or when the overlay could not be built.
    /// Setters become no-ops. The caller still Disposes it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/>, including a second call.</summary>
    bool IsDisposed { get; }

    /// <summary>Built → Shown → Hidden → Disposed. Disabled handles skip Shown.</summary>
    UiHandleState State { get; }

    /// <summary>BepInEx plugin GUID that owns the <c>progenitor-ui-*</c> root.</summary>
    string PluginId { get; }

    /// <summary>DontDestroyOnLoad root this widget lives under.</summary>
    string RootName { get; }

    /// <summary>
    /// Row alignment: <c>left</c> or <c>centre</c>. Center is the default so existing
    /// pages do not move. Defaulted so existing implementers need no change.
    /// </summary>
    string Align { get => "centre"; }

    /// <summary>
    /// SETTINGS1 stage 2: false grays the widget in place and it stops acting on input (a click, a
    /// pick, a typed commit); true (the default) restores it. Nothing is rebuilt. Defaulted so
    /// existing implementers need no change; setting it on such an implementer does nothing.
    /// </summary>
    bool Interactable { get => true; set { } }
}

/// <summary>Panel returned by <see cref="Ui.Panel"/>. Children are parented under it.</summary>
public interface IUiPanel : IUiHandle
{
    /// <summary>Clone a vanilla <c>SimpleStunButton</c> (fallback Unity Button). Click goes through the Bootstrap slot.</summary>
    IUiButton Button(string text);

    /// <summary>Clone a vanilla Toggle / <c>SettingsEntry_Checkbox</c> (fallback Unity Toggle).</summary>
    IUiToggle Toggle(string text, bool initial = false);

    /// <summary>TMP label using the game's font (evidence 88: Nocturne Serif Regular SDF).</summary>
    IUiLabel Label(string text);

    /// <summary>Clone a vanilla slider / <c>SettingsEntry_Slider</c> (fallback Unity Slider).</summary>
    IUiSlider Slider(string text, float min, float max, float initial);

    /// <summary>Clone a <c>TMP_InputField</c> / <c>SettingsEntry_TextInput</c>.</summary>
    IUiInput Input(string text, string initial = "");
    /// <summary>A search field: OnChanged fires while typing; OnSubmitted fires on Enter.</summary>
    IUiInput SearchInput(string text, string initial = "") => Input(text, initial);

    /// <summary>
    /// A finite choice: the caption on the left, a selector (previous arrow, current value, next
    /// arrow) on the right. <paramref name="labels"/>, when given, is what each of
    /// <paramref name="values"/> draws as. Each pick commits.
    /// </summary>
    IUiDropdown Dropdown(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null) =>
        UiControls.Dropdown(this, text, values, value, labels);

    /// <summary>
    /// OPTIONHOVER1: <see cref="Dropdown(string, IReadOnlyList{string}, string, IReadOnlyList{string}?)"/> with a short
    /// explanation of each option (<paramref name="descriptions"/>, index-aligned with <paramref name="values"/>; an empty
    /// entry has none). A settings page's details pane shows it while the pointer is over the option; elsewhere (a custom
    /// page, a floating window) a box beside the open list shows the highlighted option's (DROPHOVER1).
    /// </summary>
    IUiDropdown Dropdown(
        string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions) =>
        UiControls.Dropdown(this, text, values, value, labels, descriptions);

    /// <summary>
    /// A number field with steppers, clamped to <paramref name="min"/>..<paramref name="max"/> and
    /// snapped to <paramref name="step"/>, shown with <paramref name="decimals"/> places.
    /// </summary>
    IUiNumber Number(string text, double value, double min, double max, double step = 1, int decimals = 0, string units = "") =>
        UiControls.Number(this, text, value, min, max, step, decimals, units);

    /// <summary>A slider with an exact number field. Dragging commits once the slider settles.</summary>
    IUiSliderField SliderField(string text, float min, float max, float step, float value) =>
        UiControls.SliderField(this, text, min, max, step, value);

    /// <summary>A color swatch with a hex field. <paramref name="hex"/> is <c>#RRGGBB</c> (or <c>#RRGGBBAA</c> with alpha).</summary>
    IUiColorField ColorField(string text, string hex, bool alpha = false) =>
        UiControls.ColorField(this, text, hex, alpha);

    /// <summary>
    /// JOINFLOW1: a date and time stored as ISO 8601 UTC (<paramref name="isoUtc"/>, empty for none), shown
    /// and edited as a wall-clock time in <paramref name="zone"/> (this computer's zone when null) with
    /// its offset: a value box and a button that opens the picker. Done raises
    /// <see cref="IUiDateTimeField.OnChanged"/> with the new ISO UTC value. Needs a Progenitor panel
    /// (<see cref="Ui.Panel"/>); on another <see cref="IUiPanel"/> it is a disabled handle that draws nothing.
    /// </summary>
    IUiDateTimeField DateTimeField(
        string text, string isoUtc, TimeZoneInfo? zone = null, DateTimePickerPrecision precision = DateTimePickerPrecision.Minute) =>
        UiDateTimeField.Create(this, text, isoUtc, zone, precision);

    /// <summary>
    /// A capture control for the keybind <paramref name="bindId"/> that plugin
    /// <paramref name="pluginId"/> registered with <c>ctx.Keybinds</c> / <see cref="Keybinds"/>.
    /// </summary>
    IUiKeyField KeyField(string text, string pluginId, string bindId) =>
        UiControls.KeyField(this, text, pluginId, bindId);

    /// <summary>
    /// SETTINGS1 stage 2: a text field <paramref name="lines"/> lines tall (2–12). Enter starts a new
    /// line; the text commits once, when the field loses focus (<see cref="IUiInput.OnChanged"/>).
    /// </summary>
    IUiInput TextArea(string text, string initial = "", int lines = 3) =>
        UiSettingsControls.TextArea(this, text, initial, lines);

    /// <summary>
    /// SETTINGS1 stage 2, RADIO1: a one-of choice with every option visible, drawn as a radio list: one option
    /// per line with a radio circle (filled for the chosen one), the chosen line bright and the others dimmed. A
    /// click on a line chooses it (one write, never a toggle).
    /// </summary>
    IUiRadio Radio(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null) =>
        UiSettingsControls.Radio(this, text, values, value, labels);

    /// <summary>
    /// RADIO1: <see cref="Radio(string, IReadOnlyList{string}, string, IReadOnlyList{string}?)"/> with a one-line muted
    /// explanation under each option (<paramref name="descriptions"/>, index-aligned with <paramref name="values"/>;
    /// an empty entry draws none).
    /// </summary>
    IUiRadio Radio(
        string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions) =>
        UiSettingsControls.Radio(this, text, values, value, labels, descriptions);

    /// <summary>SETTINGS1 stage 2: a set of choices, one check per option, each click a write.</summary>
    IUiMultiSelect MultiSelect(
        string text, IReadOnlyList<string> values, IEnumerable<string>? selected, IReadOnlyList<string>? labels = null) =>
        UiSettingsControls.MultiSelect(this, text, values, selected, labels);

    /// <summary>
    /// SETTINGS1 stage 2: a read-only line whose value is <paramref name="value"/>, re-read every
    /// <paramref name="refreshSeconds"/> (at least 0.25; 0 reads once) while the line is visible.
    /// A function that throws is logged once and the last value stays.
    /// </summary>
    IUiInfo Info(string text, Func<string> value, double refreshSeconds = 1) =>
        UiSettingsControls.Info(this, text, value, refreshSeconds);

    /// <summary>SETTINGS1 stage 2: a thin horizontal rule between groups of rows.</summary>
    IUiLabel Divider() => UiSettingsControls.Divider(this);

    /// <summary>
    /// SETTINGS1 stage 2: a button that asks <paramref name="question"/> in the kit's confirmation
    /// dialog before its <see cref="IUiButton.OnClick"/> handler runs; <b>No</b> or Escape runs
    /// nothing. The dialog closes with the button (a page rebuild, the panel closing, the plugin
    /// unloading), and a late Yes then runs nothing. Needs a Progenitor panel (<see cref="Ui.Panel"/>);
    /// another <see cref="IUiPanel"/> implementation throws <see cref="NotSupportedException"/>.
    /// </summary>
    IUiButton ConfirmButton(string text, string question) =>
        UiSettingsControls.ConfirmButton(this, text, question);

    /// <summary>
    /// DIALOGS1: <see cref="ConfirmButton(string, string)"/> with a muted <paramref name="body"/> line
    /// under the question — the same dialog every Progenitor confirmation uses (the question as its
    /// title, <b>No</b> on the left, the button's caption on the right).
    /// </summary>
    IUiButton ConfirmButton(string text, string question, string body) =>
        UiSettingsControls.ConfirmButton(this, text, question, body: body);

    /// <summary>
    /// Unlock the cursor and start input capture (Bootstrap <c>UiInputContext</c>).
    /// Capture is taken only on this panel's 0→1 <c>Show</c>; a second Show is a no-op
    /// for the refcount so one Hide returns capture to 0.
    ///
    /// <para>
    /// The cursor unlock lasts the whole time the panel is shown. Whether gameplay input is also
    /// <i>blocked</i> is decided separately by <see cref="CaptureMode"/>, which by default follows
    /// the pointer (job FUI19).
    /// </para>
    /// </summary>
    void Show();

    /// <summary>Hide the panel and release capture on this panel's 1→0 transition.</summary>
    void Hide();

    /// <summary>
    /// Alignment of subsequent rows: <c>left</c> or <c>centre</c>. Center is the default.
    /// Left: label on the left, control on the right of the same row, notes dimmed under
    /// the label, section headings distinct. Defaulted so existing implementers need no change.
    /// </summary>
    string ContentAlignment { get => "centre"; set { } }

    /// <summary>Section heading: larger/bolder, separator, extra top spacing when the kit supports it.</summary>
    IUiLabel Heading(string text) => Label(text);

    /// <summary>Secondary / status line under a label, dimmer when the kit supports it.</summary>
    IUiLabel Note(string text) => Label(text);

    /// <summary>Subsequent field and action buttons share one row (path + Apply + Clear).</summary>
    void BeginInline() { }

    /// <summary>End a <see cref="BeginInline"/> cluster.</summary>
    void EndInline() { }

    /// <summary>Compact name/value pair. Falls back to a <c>name: value</c> label.</summary>
    IUiLabel Fact(string name, string value) => Label((name ?? "") + ": " + (value ?? ""));

    /// <summary>
    /// When this panel blocks gameplay input. Defaults to this plugin's default and then to the
    /// framework's, which is <see cref="UiCaptureMode.WhenHovered"/> — the wheel scrolls the panel
    /// under the pointer and zooms the camera everywhere else. Set
    /// <see cref="UiCaptureMode.WhileShown"/> for a genuinely modal panel. Settable on an open
    /// panel; it takes effect immediately. See <see cref="Ui.CaptureInput(UiCaptureMode, object?)"/>
    /// for the plugin-wide and framework-wide defaults.
    ///
    /// <para>
    /// One consequence worth knowing before choosing <see cref="UiCaptureMode.Never"/>: with that
    /// mode Escape closes the panel <b>and</b> opens the vanilla system menu, because nothing is
    /// registered to consume the key. The cursor unlock and the Escape forwarding come from the
    /// capture refcount every shown panel drives; consuming the key needs an input context, which
    /// only the other two modes ask for (job FUI23, R44 finding 6).
    /// </para>
    /// </summary>
    UiCaptureMode CaptureMode { get; set; }

    /// <summary>
    /// One small, muted line centered at the bottom of the panel, below the last row and outside
    /// the scrolling content, so it never pushes a button out of view. Empty (the default for a
    /// plugin's panel) draws nothing. Every prompt Progenitor itself shows sets this to
    /// <see cref="Ui.FrameworkFooter"/>; a plugin opts in with
    /// <c>panel.Footer = Ui.FrameworkFooter</c>, or shows its own version the same way
    /// (<c>panel.Footer = "MyMod " + MyPluginInfo.PLUGIN_VERSION</c>). Settable at any time; the
    /// panel re-lays out. Defaulted so existing implementers need no change.
    /// </summary>
    string Footer { get => ""; set { } }
}

/// <summary>Clickable cloned button. <see cref="OnClick"/> is the Bootstrap slot, not a collectible UnityAction.</summary>
public interface IUiButton : IUiHandle
{
    /// <summary>
    /// Subscribe a click handler. The UnityEvent listener is a Bootstrap trampoline keyed
    /// by this widget's ushort id; <paramref name="handler"/> lives in Core / the plugin
    /// and is invoked from that trampoline. Dispose the returned lease or the button to unsubscribe.
    /// </summary>
    IDisposable OnClick(Action handler);
}

/// <summary>Boolean toggle. <see cref="Value"/> is the Unity Toggle state.</summary>
public interface IUiToggle : IUiHandle
{
    bool Value { get; set; }

    IDisposable OnChanged(Action<bool> handler);
}

/// <summary>Static TMP label.</summary>
public interface IUiLabel : IUiHandle
{
}

/// <summary>
/// KIT1: a label whose caption carries a <see cref="LabelTone"/> color (the mod page's status word,
/// an action caption, a warning line). Made with <see cref="UiLook.Label"/>.
/// </summary>
public interface IUiToneLabel : IUiLabel
{
    /// <summary>The tone last applied.</summary>
    LabelTone Tone { get; }

    /// <summary>Recolor in place. No relayout: only the TMP color changes.</summary>
    void SetTone(LabelTone tone);

    /// <summary>Rewrite the caption and its tone together — the pair a status change always is.</summary>
    void SetText(string text, LabelTone tone);

    /// <summary><c>widget_read</c>: <c>{kind: label, text, tone}</c>.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>
/// KIT1: a small square icon-only button — the mock's exit-link at the top right of a section. The
/// caption is a WGL4-safe glyph today (<see cref="IUiIconLink.Glyph"/>); <see cref="SetSprite"/> is
/// the slot a real link sprite takes later without the callers changing.
/// </summary>
internal interface IUiIconLink : IUiButton
{
    /// <summary>The glyph drawn now, or empty once a sprite replaces it.</summary>
    string Glyph { get; }

    /// <summary>Hover text. Also the readback's <c>tooltip</c>, so a reader knows what the icon does.</summary>
    string Tooltip { get; }

    /// <summary>What the link opens. Empty when nothing is known; carried in the readback either way.</summary>
    string Url { get; set; }

    /// <summary>Give the link a <c>UnityEngine.Sprite</c> (or null to go back to the glyph).</summary>
    void SetSprite(object? sprite);

    /// <summary><c>widget_read</c>: <c>{kind: link, tooltip, url}</c>.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>
/// KIT1: a sidebar row with a marker in front of its caption and a state dot at its right edge
/// (MODS1's row, drawn the mock's way at last: the marker red, the dot the state's color, the
/// caption ellipsized in the space between). Two-line sidebar rows remain a gap.
/// </summary>
internal interface IUiMarkedButton : IUiButton
{
    /// <summary>The marker in front of the caption (the mock's <c>(!)</c>), or empty.</summary>
    string Marker { get; }

    /// <summary>The dot's tone, or null for no dot.</summary>
    LabelTone? Dot { get; }

    /// <summary>The caption between them, as the row was asked to draw it.</summary>
    string Caption { get; }

    void SetMarker(string? marker);

    void SetDot(LabelTone? dot);

    /// <summary><c>widget_read</c>: <c>{kind: button, label, marker, dot}</c>.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>Float slider. <see cref="Value"/> is the Unity Slider value.</summary>
public interface IUiSlider : IUiHandle
{
    float Value { get; set; }

    IDisposable OnChanged(Action<float> handler);
}

/// <summary>TMP input field. <see cref="IUiHandle.Text"/> and <see cref="Value"/> are the same string.</summary>
public interface IUiInput : IUiHandle
{
    string Value { get; set; }

    IDisposable OnChanged(Action<string> handler);
    /// <summary>Enter in this field; blurring alone does not submit.</summary>
    IDisposable OnSubmitted(Action<string> handler) => OnChanged(handler);
    /// <summary>Focus a visible field, optionally selecting its existing text.</summary>
    void Focus(bool selectAll = true) { }
}

/// <summary>
/// One full-width row of positioned cells (LIST1). Internal: the Install list view's
/// primitive, promoted to the public kit only once LIST2's live view passes. Create through
/// <see cref="UiCellRows.CellRow"/> / <see cref="UiCellRows.CellHeader"/>. Cells are rewritten
/// in place (<see cref="SetCell"/>) without a panel relayout; the row's height never changes
/// after creation. <see cref="IUiHandle.Text"/> is the row key.
/// </summary>
internal interface IUiCellRow : IUiHandle
{
    /// <summary>The row key from its <see cref="CellRowSpec"/>.</summary>
    string Key { get; }

    /// <summary>The live cell specs (text, tone, visibility as last written).</summary>
    IReadOnlyList<CellSpec> Cells { get; }

    /// <summary>
    /// INSTALLUX1 item 4: the row's <see cref="CellRowSpec.RightInset"/> — width at its right-hand
    /// end no cell may enter (the pane's scrollbar band). Zero for a row that wants the whole
    /// width. Exposed so a geometry test can measure the placed row rather than a spec it rebuilt.
    /// </summary>
    float RightInset { get; }

    bool Expanded { get; }

    bool Selected { get; }

    /// <summary>A click on the row outside a button cell.</summary>
    IDisposable OnRowClick(Action handler);

    /// <summary>A click on the button cell <paramref name="cellKey"/> (or a linked text cell, <see cref="CellSpec.Linkify"/>). Non-button keys get a no-op lease.</summary>
    IDisposable OnClick(string cellKey, Action handler);

    /// <summary>
    /// JOINLINKS1: in a linked cell's click handler, the link under the pointer (its index in
    /// <see cref="CellSpec.LinksNow"/>), or -1 when the click missed every link.
    /// </summary>
    int LinkAt(string cellKey) => -1;

    /// <summary>
    /// KEYS3: the pointer entered (<c>true</c>) or left (<c>false</c>) the row. Only a row built with
    /// <see cref="CellRowSpec.Hover"/> reports it; any other row, a header and a disabled row get a
    /// no-op lease. Moving onto one of the row's own buttons does not count as leaving it.
    /// </summary>
    IDisposable OnHover(Action<bool> handler);

    /// <summary>
    /// KEYS3 polish: the pointer entered or left the button cell <paramref name="cellKey"/>, which must be
    /// built with <see cref="CellSpec.Hover"/>; any other cell gets a no-op lease.
    /// </summary>
    IDisposable OnCellHover(string cellKey, Action<bool> handler);

    /// <summary>
    /// The value of the <see cref="CellKind.Input"/> cell <paramref name="cellKey"/> changed
    /// (per keystroke; the cell's <see cref="CellSpec.Text"/> already carries the new value).
    /// Non-input keys get a no-op lease. LIST3.
    /// </summary>
    IDisposable OnChanged(string cellKey, Action<string> handler);

    /// <summary>Rewrite one cell in place. Null arguments leave that facet alone. False when the key is unknown.</summary>
    bool SetCell(string key, string? text = null, CellTone? tone = null, bool? visible = null);

    /// <summary>Give an image cell its sprite (a <c>UnityEngine.Sprite</c> or null). Repaints in place.</summary>
    bool SetImage(string key, object? sprite);

    /// <summary>Rotate the chevron cell(s).</summary>
    void SetExpanded(bool expanded);

    /// <summary>Selected fill and the left gold stripe.</summary>
    void SetSelected(bool selected);

    /// <summary>The <c>widget_read</c> row: kind, key, cells (key, kind, text, tone, visible, rect), expanded, selected.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>The header variant: uppercase muted captions, sortable columns.</summary>
internal interface IUiCellHeader : IUiCellRow
{
    /// <summary>The column currently marked sorted (its caption carries the ▼ indicator), or null.</summary>
    string? SortedKey { get; }

    /// <summary>Mark <paramref name="sortKey"/>'s column sorted. Unknown keys clear the indicator.</summary>
    void SetSorted(string? sortKey);

    /// <summary>INSTALLCOLS1: the sorted column reads ▲ (ascending) rather than ▼ (descending).</summary>
    bool SortAscending => false;

    /// <summary>
    /// INSTALLCOLS1: mark <paramref name="sortKey"/>'s column sorted in a direction — ▲ when
    /// <paramref name="ascending"/>, ▼ otherwise. The default ignores the direction.
    /// </summary>
    void SetSorted(string? sortKey, bool ascending) => SetSorted(sortKey);

    /// <summary>A click on a sortable column; the argument is that cell's <see cref="CellSpec.SortKey"/>.</summary>
    IDisposable OnSort(Action<string> handler);
}

/// <summary>
/// <c>panel.CellRow(spec)</c> / <c>panel.CellHeader(cells)</c>. Extension methods rather
/// than <see cref="IUiPanel"/> members so the cell types stay internal and out of the
/// published API (docs/api.md) until LIST2's live acceptance.
/// </summary>
internal static class UiCellRows
{
    /// <summary>One row of positioned cells. Disabled (state kept, nothing drawn) on a panel this runtime cannot draw.</summary>
    internal static IUiCellRow CellRow(this IUiPanel panel, CellRowSpec spec) =>
        panel is UiPanelHandle host ? host.AddCellRow(spec, header: false) : UiCellRowHandle.Disabled(panel, spec, header: false);

    /// <summary>The header row for the same cells: uppercase muted captions; cells with a <see cref="CellSpec.SortKey"/> are clickable.</summary>
    /// <param name="rightInset">
    /// INSTALLUX1 item 4: width at the row's right-hand end no header cell may enter
    /// (<see cref="CellRowSpec.RightInset"/>) — the pane's scrollbar band. A header whose last
    /// column ends on the row edge does not sit above the rows when those are inset.
    /// </param>
    internal static IUiCellHeader CellHeader(
        this IUiPanel panel, IReadOnlyList<CellSpec> cells, string key = "header", float rightInset = 0f) =>
        (IUiCellHeader)(panel is UiPanelHandle host
            ? host.AddCellRow(new CellRowSpec(key, cells, rightInset: rightInset), header: true)
            : UiCellRowHandle.Disabled(panel, new CellRowSpec(key, cells, rightInset: rightInset), header: true));
}

/// <summary>
/// The game's look for a mod's own controls, on a custom Mods page or a floating window: a button drawn as the game's
/// standard button, and a label in one of the framework's status colors. Kit controls added with the ordinary
/// <see cref="IUiPanel"/> calls keep the kit's look unless asked, on a custom page too.
/// </summary>
public static class UiLook
{
    /// <summary>
    /// GAMEWINDOW1 (decided 2026-10-10): draw this floating window in the game's look, as a Mods page is: the game's
    /// popup frame, and the controls added after this call on the settings grid (name left, the control in a fixed column,
    /// a toggle as the game's checkbox), buttons as the game's compact button at the row's left edge, side by side in an
    /// inline block. Call it before adding controls; controls added earlier keep the kit's look. The window keeps its
    /// title and is placed like any other panel (not modal). Returns the panel, so a call can chain.
    /// </summary>
    public static IUiPanel GameWindow(this IUiPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (panel is not UiPanelHandle host || host.IsDisposed) return panel;
        host.GridControls = true;
        if (host.Enabled && host.Runtime is IRuntimePanel runtimePanel)
        {
            try { Ui.CurrentRuntime().GameWindowChrome(runtimePanel); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: game window chrome: " + e.Message); }
        }

        return panel;
    }

    /// <summary>Game-styled window with a requested width, capped to the available canvas.
    /// Call before adding controls. Wider windows are centered.</summary>
    public static IUiPanel GameWindow(this IUiPanel panel, float width)
    {
        panel.GameWindow();
        // A wider frame is centered by the runtime, never flowed beside the standard windows.
        if (panel is UiPanelHandle tagged && width > UnityUiRuntimeMetrics.PanelWidth) tagged.Flows = false;
        if (panel is UiPanelHandle host && !host.IsDisposed && host.Runtime is IRuntimePanel runtimePanel)
            Ui.CurrentRuntime().SetDialogWidth(runtimePanel, width);
        return panel;
    }

    /// <summary>
    /// Draw <paramref name="button"/> as the game's standard button: the game's steel button art, the caption centered.
    /// Until the borrowed art is ready (at the first open, or just after a Core swap) the button shows a flat steel face
    /// and the art follows on a later frame. Works on any host. Returns the button, so a call can chain:
    /// <c>panel.Button("Apply").GameStyle().OnClick(...)</c>.
    /// </summary>
    public static IUiButton GameStyle(this IUiButton button)
    {
        if (button == null) throw new ArgumentNullException(nameof(button));
        button.GameButton();
        return button;
    }

    /// <summary>
    /// A label in <paramref name="tone"/>'s color: a status word, an action caption, a warning line. Recolor it in place
    /// with <see cref="IUiToneLabel.SetTone"/>, or change text and tone together with <see cref="IUiToneLabel.SetText"/>.
    /// Disabled (state kept, nothing drawn) on a panel this runtime cannot draw, exactly like every other handle.
    /// </summary>
    public static IUiToneLabel Label(this IUiPanel panel, string text, LabelTone tone) =>
        panel is UiPanelHandle host
            ? host.AddToneLabel(text ?? "", tone)
            : UiToneLabelHandle.Disabled(panel, tone);
}

/// <summary>
/// KIT1: the composed blocks a detail page is built out of — the two-column head, a colored label,
/// an icon link, a sidebar row with a marker and a dot, and the in-pane y/n prompt. Extension methods
/// rather than <see cref="IUiPanel"/> members so the tones, the cell types and the block handles stay
/// out of the published API (docs/api.md) until the redesigned Mods page passes live.
/// </summary>
internal static class UiKitBlocks
{
    /// <summary>
    /// A small square icon-only button. <paramref name="attachToHeading"/> puts it in the previous
    /// row's right-hand column — the mock's link beside a section heading — and falls back to its
    /// own right-aligned row when there is no row to attach to.
    /// </summary>
    internal static IUiIconLink IconLink(
        this IUiPanel panel,
        string glyph,
        string tooltip,
        Action? onClick = null,
        string url = "",
        bool attachToHeading = true)
    {
        var link = panel is UiPanelHandle host
            ? host.AddIconLink(glyph ?? "", tooltip ?? "", attachToHeading)
            : UiIconLinkHandle.Disabled(panel, glyph ?? "", tooltip ?? "");
        link.Url = url ?? "";
        if (onClick != null) link.OnClick(onClick);
        return link;
    }

    /// <summary>
    /// A sidebar row: <paramref name="marker"/> in front of the caption in red, <paramref name="dot"/>
    /// at the right edge in its tone's color, the caption ellipsized between them.
    /// </summary>
    internal static IUiMarkedButton Button(this IUiPanel panel, string caption, string? marker, LabelTone? dot) =>
        panel is UiPanelHandle host
            ? host.AddMarkedButton(caption ?? "", marker ?? "", dot)
            : UiMarkedButtonHandle.Disabled(panel, caption ?? "", marker ?? "", dot);

    /// <summary>
    /// The two-column head block of a detail page. <paramref name="rowWidth"/> is the pane's row
    /// width; 0 asks <see cref="MenuPanelLayout.DetailRowWidth"/>, which is what a caller that is
    /// not measuring the pane itself wants.
    /// </summary>
    internal static UiHeadBlock HeadBlock(this IUiPanel panel, HeadSpec spec, float rowWidth = 0f) =>
        UiHeadBlock.Build(panel, spec, rowWidth);

    /// <summary>
    /// The in-pane y/n prompt as one block: heading, muted body, and the two buttons right-aligned on
    /// one row. <paramref name="tag"/> is what the caller's readback keys the three rows by.
    /// </summary>
    internal static UiPromptBlock Prompt(
        this IUiPanel panel,
        string tag,
        string heading,
        string body,
        string yesCaption,
        LabelTone yesTone,
        string noCaption) =>
        UiPromptBlock.Build(panel, tag, heading, body, yesCaption, yesTone, noCaption);
}

/// <summary>Handle lifecycle. Tests assert Panel → Show → Hide → Dispose; double dispose stays Disposed.</summary>
public enum UiHandleState
{
    /// <summary>Server, skip, or missing overlay. No Unity object was created.</summary>
    Disabled = 0,

    /// <summary>Client panel/widget exists; not currently shown.</summary>
    Built = 1,

    /// <summary>Visible and capturing input.</summary>
    Shown = 2,

    /// <summary>Released. Further Dispose calls are ignored.</summary>
    Disposed = 3,
}

/// <summary>Bootstrap <c>UiSlots</c> kind byte. Numeric values are the contract with Bootstrap.</summary>
public enum UiSlotKind : byte
{
    Click = 1,
    Float = 2,
    Bool = 3,
    Text = 4,
    PointerEnter = 5,
    PointerExit = 6,
}
