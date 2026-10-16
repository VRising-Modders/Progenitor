using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Unity (or a test fake) behind <see cref="Ui"/>. Tests inject a fake so they never
/// need the game; the client uses <c>UnityUiRuntime</c>. Every method swallows Unity
/// exceptions and returns a disabled/empty result rather than throw into a hook.
/// </summary>
internal interface IUiRuntime
{
    /// <summary>False in a VRisingServer process. Server Panel/Show must not touch Unity.</summary>
    bool IsClient { get; }

    IRuntimePanel? CreatePanel(string title, string rootName);

    IRuntimeVanillaTabs? CreateVanillaTabs(VanillaUiSite site, string[] captions, Action<int> selected) => null;

    /// <summary>Opt an existing panel into the Mods workspace; ordinary panels are unaffected.</summary>
    void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar) { }

    /// <summary>
    /// HOSTS1 round 5: a soft-hidden panel keeps its objects active when hidden (only its canvas and
    /// raycaster are switched off), so showing it again rebuilds no text meshes. For a pane kept between
    /// opens (the Mods list). Default: no soft hide.
    /// </summary>
    void SetSoftHide(IRuntimePanel panel, bool soft) { }

    /// <summary>
    /// As <see cref="ConfigureMenuPanel(IRuntimePanel, bool)"/> with the pane's
    /// <see cref="MenuPaneMode"/> (LIST3: the Install tab's full-width, title-less pane).
    /// Default: the split configuration, so existing fakes need no change.
    /// </summary>
    void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar, MenuPaneMode mode) => ConfigureMenuPanel(panel, sidebar);

    /// <summary>
    /// Place an existing panel as the Mods workspace tab strip
    /// (<see cref="MenuPanelLayout.ForStrip"/>). No-op on a runtime that does not
    /// host Unity panels. Ordinary panels and the two workspace panes are unaffected.
    /// </summary>
    void ConfigureMenuStrip(IRuntimePanel panel) { }

    /// <summary>
    /// Size the Mods workspace to the native tab content frame (when one exists) and
    /// put the embedded backdrop behind both panes. No-op on a runtime that does not
    /// host Unity panels. Idempotent while the workspace is open.
    /// </summary>
    void EnsureModsWorkspaceChrome() { }

    /// <summary>
    /// Drop the Mods workspace frame and backdrop, including any Unity texture created
    /// from the embedded PNG. Called on Close and Core Uninstall. Idempotent.
    /// </summary>
    void ReleaseModsWorkspaceChrome() { }

    /// <summary>
    /// Where subsequent rows on a menu pane are parented. Content rows scroll; top/bottom
    /// chrome stays put. No-op on a runtime that does not host Unity panels.
    /// </summary>
    void PinMenuRows(IRuntimePanel panel, MenuRowPin pin) { }

    /// <summary>
    /// SETTINGS1 stage 3: put <paramref name="text"/> on the system clipboard. False when this runtime
    /// cannot (a game-free host); the caller then shows the text and says so.
    /// </summary>
    bool TryCopyToClipboard(string text) => false;

    /// <summary>SETTINGS1 stage 3: whether <see cref="TryCopyToClipboard"/> can work here at all.</summary>
    bool CanCopyToClipboard => false;

    /// <summary>
    /// Visual treatment of the next sidebar button. No-op on a runtime that does not
    /// host Unity panels.
    /// </summary>
    void StyleMenuRow(IRuntimePanel panel, MenuRowTone tone) { }

    /// <summary>
    /// KIT1 gen 1: drop this pane's own centered title band, because the page draws its own name.
    /// The same thing <see cref="MenuPaneMode.FullWidth"/> does for the Install tab (LIST3 item 8),
    /// but without changing the pane's geometry — the mod page is still a
    /// <see cref="MenuPaneMode.Split"/> detail pane beside the sidebar. A pane asks once, while it is
    /// being built; the panes that want a title (the overview, a custom page) simply do not ask.
    /// No-op on a runtime that does not host Unity panels.
    /// </summary>
    void HidePaneTitle(IRuntimePanel panel) { }

    /// <summary>
    /// CONSENT2: this panel places and sizes its own frame, so neither the layout pass nor the
    /// re-flow may write its <c>RectTransform</c>. Everything else about it — rows, scroll, input
    /// capture, Escape, the settle re-measure — is unchanged.
    ///
    /// <para><b>Why a modal dialog needs it.</b> <c>PanelFlow</c> gives each shown panel a rank and
    /// a slot, which is right for the framework's side-by-side settings panels and wrong for the
    /// consent prompt: it is centered over the screen and is not one of a set. Live on 2026-09-13
    /// the prompt was revealed correctly centered at <c>rank=0/1 at=(0,0)</c> and then, the moment
    /// the Mods workspace's panels joined the set, re-flowed to <c>rank=0/2 at=(-332,0)</c> — the
    /// player's "it flickers in from the left". The prompt's own frame poll re-centered it a frame
    /// later, which is the flicker rather than a fix for it.</para>
    ///
    /// <para>No-op on a runtime that does not host Unity panels.</para>
    /// </summary>
    void SelfPlacePanel(IRuntimePanel panel, bool selfPlaced) { }

    /// <summary>
    /// P7b: this panel never scrolls (the pick-in-world band: a zero-sized frame whose visible band is a child). Its
    /// ScrollRect and bar are switched off for good, and the scroll-bar read-back skips it, since PanelLayout's
    /// fits/scrolls verdict says nothing about a panel that has no scroll view. No-op on a runtime without Unity panels.
    /// </summary>
    void NoScrollPanel(IRuntimePanel panel) { }

    /// <summary>
    /// INSTALLUX2 item 1: make this panel a modal dialog — an opaque frame on the mods palette,
    /// a raycast-blocking backdrop behind it on the panel's own overlay canvas, content-sized and
    /// centered, no scrollbar while the content fits, and revealed only after the runtime's own
    /// layout pass has placed it (the CONSENT2 rule, as a kit seam rather than one panel's
    /// private code). Implies <see cref="SelfPlacePanel"/>: the dialog owns its rect.
    ///
    /// <para>No-op on a runtime that does not host Unity panels; a test fake records the ask.</para>
    /// </summary>
    void DialogChrome(IRuntimePanel panel) { }

    /// <summary>
    /// GAMEWINDOW1: a floating window in the game's look: the game's popup frame (the art the dialogs use), its title kept,
    /// placed and tiled like any panel, with no backdrop (not modal). No-op on a runtime that does not host Unity panels.
    /// </summary>
    void GameWindowChrome(IRuntimePanel panel) { }

    /// <summary>
    /// TOKENINSERT1: insert <paramref name="text"/> into the text field <paramref name="field"/> at its caret (the end when
    /// it never had focus; a selection is replaced), whole or not at all (<see cref="UiFieldInsert"/>), through the
    /// field's own edit path (undo, change notice), and give the field the keyboard. False when the runtime cannot (a
    /// test fake): the caller appends instead.
    /// </summary>
    bool InsertText(IRuntimeWidget field, string text) => false;

    /// <summary>WIDEDIALOG1: a dialog's frame width (<see cref="DialogLayout.FrameWidthFor"/>); the default draws nothing.</summary>
    void SetDialogWidth(IRuntimePanel panel, float frameWidth) { }

    /// <summary>
    /// Draw <paramref name="text"/> as the panel's footer: one small muted line centered at the
    /// bottom of the frame, in a band the scrolling viewport is inset above (so the last row and
    /// its buttons stay in view), placed below the last row. Empty removes it and gives the band
    /// back. On a Mods workspace detail pane the line is centered on the workspace frame, not on
    /// the pane. A self-placed panel owns its own insets and reserves the band itself
    /// (<see cref="PanelFooterLayout.BandFor"/>). No-op on a runtime that does not host Unity
    /// panels.
    /// </summary>
    void SetPanelFooter(IRuntimePanel panel, string text) { }

    /// <summary>
    /// Subsequent rows use <c>left</c> or <c>centre</c> (default) caption alignment.
    /// Defaulted so existing test fakes need no change.
    /// </summary>
    void SetContentAlignment(IRuntimePanel panel, string alignment) { }

    /// <summary>Section heading. Default: a label.</summary>
    IRuntimeWidget? AddHeading(IRuntimePanel panel, string text) => AddLabel(panel, text);

    /// <summary>Dimmer secondary line. Default: a label.</summary>
    IRuntimeWidget? AddMutedLabel(IRuntimePanel panel, string text) => AddLabel(panel, text);

    /// <summary>
    /// DIALOGS1: a dialog's title (<paramref name="title"/> true: bold, <see cref="DialogLayout.TitleFontSize"/>)
    /// or body (muted, <see cref="DialogLayout.BodyFontSize"/>, wrapping inside
    /// <see cref="DialogLayout.BodyWidth"/>) — centered, wrapping, no rule and no extra top space.
    /// Default: a heading or a note, so an existing runtime or fake compiles unchanged.
    /// </summary>
    IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title) =>
        title ? AddHeading(panel, text) : AddMutedLabel(panel, text);

    /// <summary>
    /// DIALOGS3: a dialog's note under its facts: body text with <see cref="DialogLayout.NoteTopMargin"/>
    /// above it. Default: the body text.
    /// </summary>
    IRuntimeWidget? AddDialogNote(IRuntimePanel panel, string text) => AddDialogText(panel, text, false);

    /// <summary>Name/value pair. Default: <c>name: value</c> label.</summary>
    IRuntimeWidget? AddFact(IRuntimePanel panel, string name, string value) =>
        AddLabel(panel, (name ?? "") + ": " + (value ?? ""));

    /// <summary>Subsequent input and buttons share one row. Default no-op.</summary>
    void BeginInlineControls(IRuntimePanel panel) { }

    /// <summary>End <see cref="BeginInlineControls"/>.</summary>
    void EndInlineControls(IRuntimePanel panel) { }

    IRuntimeWidget? AddButton(IRuntimePanel panel, string text);

    /// <summary>
    /// One full-width row of positioned cells (LIST1: <see cref="CellRowSpec"/>), or the
    /// uppercase header variant when <paramref name="header"/>. Default null so existing
    /// test fakes need no change; the handle then keeps its state without a Unity object.
    /// </summary>
    IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header) => null;

    /// <summary>
    /// KIT1: a small square icon-only button (a section's exit link). <paramref name="attachToHeading"/>
    /// asks for it in the previous row's right-hand column — the mock draws the link beside the
    /// heading, not under it — and a runtime that cannot do that puts it on its own row instead.
    /// Default: an ordinary button captioned with the glyph, so existing fakes need no change.
    /// </summary>
    IRuntimeWidget? AddIconLink(IRuntimePanel panel, string glyph, string tooltip, bool attachToHeading) =>
        AddButton(panel, glyph);

    /// <summary>
    /// KIT1: give an icon link a <c>UnityEngine.Sprite</c> in place of its glyph (null clears it).
    /// False when this runtime has no image to put it in, which is what keeps the glyph drawn.
    /// </summary>
    bool SetIconSprite(IRuntimeWidget widget, object? sprite) => false;

    /// <summary>
    /// KIT1: a sidebar row with <paramref name="marker"/> in front of the caption and a
    /// <paramref name="dot"/> at its right edge. Default: an ordinary button, so a runtime without
    /// the two extra parts still draws a usable row.
    /// </summary>
    IRuntimeWidget? AddMarkedButton(IRuntimePanel panel, string caption, string marker, LabelTone? dot) =>
        AddButton(panel, caption);

    /// <summary>KIT1: rewrite a marked row's marker and dot in place. Default no-op.</summary>
    void StyleMarkedButton(IRuntimeWidget widget, string marker, LabelTone? dot) { }

    IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial);

    IRuntimeWidget? AddLabel(IRuntimePanel panel, string text);

    /// <summary>
    /// KIT1: a label whose caption is colored by <paramref name="tone"/>
    /// (<see cref="MenuPalette.LabelColor"/>). Default: the plain label, so existing fakes need no
    /// change and a runtime with no color still shows the words. Not an <c>AddLabel</c> overload:
    /// the source guards that pin the label path address a builder by name
    /// (<c>LabelTmpWriteTests</c>), and an overload would answer them instead of the row builder.
    /// </summary>
    IRuntimeWidget? AddTonedLabel(IRuntimePanel panel, string text, LabelTone tone) => AddLabel(panel, text);

    /// <summary>KIT1: recolor an existing label in place. Default no-op.</summary>
    void StyleLabel(IRuntimeWidget widget, LabelTone tone) { }

    IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial);

    IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial);
    IRuntimeWidget? AddSearchInput(IRuntimePanel panel, string text, string initial) => AddInput(panel, text, initial);
    bool WireInputSubmit(IRuntimeWidget input, ushort slot) => false;
    void FocusInput(IRuntimeWidget input, bool selectAll) { }

    /// <summary>
    /// Sidebar search box, if this runtime has one. Escape uses it to clear a focused
    /// non-empty field before the workspace hide sweep. <paramref name="focused"/> is
    /// owned activation (click / type / restore), not EventSystem selection: Cancel
    /// on Escape drops TMP <c>isFocused</c> before the hide sweep runs. Default: no field.
    /// </summary>
    bool TryReadSearchField(out string text, out bool focused)
    {
        text = "";
        focused = false;
        return false;
    }

    /// <summary>
    /// SEARCHCLEAR1: the focused workspace search box (the Mods search or the Install search) gives up
    /// focus, as a click outside it does; Escape on an empty box. False when no search box had focus.
    /// Default: no field.
    /// </summary>
    bool LeaveSearchField() => false;

    /// <summary>Dropdown / finite choice. Default null so existing test fakes need no change.</summary>
    IRuntimeWidget? AddChoice(
        IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value) => null;

    /// <summary>Numeric field + stepper. Default null so existing test fakes need no change.</summary>
    IRuntimeWidget? AddNumeric(
        IRuntimePanel panel, string text, double value, double min, double max, double step, int decimals, string units) => null;

    /// <summary>Slider + exact numeric. Default null so existing test fakes need no change.</summary>
    IRuntimeWidget? AddSliderEntry(
        IRuntimePanel panel, string text, float min, float max, float step, float initial) => null;

    /// <summary>Color swatch + hex. Default null so existing test fakes need no change.</summary>
    IRuntimeWidget? AddColorPicker(IRuntimePanel panel, string text, UiColor initial, bool alpha) => null;

    /// <summary>Keybind capture row. Default null so existing test fakes need no change.</summary>
    IRuntimeWidget? AddKeybindPicker(IRuntimePanel panel, string text, string pluginId, string bindId) => null;

    /// <summary>
    /// SETTINGS1 stage 2: a text field <paramref name="lines"/> lines tall whose Enter inserts a new
    /// line; its text event is the end-of-edit. Default: an ordinary one-line input.
    /// </summary>
    IRuntimeWidget? AddTextArea(IRuntimePanel panel, string text, string initial, int lines) =>
        AddInput(panel, text, initial);

    /// <summary>
    /// SETTINGS1 stage 2: a radio group or multi-select row. Each option is a clickable part keyed
    /// <see cref="UiOptionsRules.PartKey"/> (<see cref="IRuntimePartsWidget"/>). Default null so
    /// existing fakes need no change; the handle then keeps its state without a Unity object.
    /// </summary>
    IRuntimeWidget? AddOptions(
        IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, bool multi, UiOptionsLayout layout) => null;

    /// <summary>
    /// SETTINGS1 stage 2: a settings-grid button row — <paramref name="name"/> in the caption column,
    /// a normal-size button captioned <paramref name="caption"/> in the control column. Default: an
    /// ordinary button captioned <paramref name="caption"/>.
    /// </summary>
    IRuntimeWidget? AddGridButton(IRuntimePanel panel, string name, string caption) => AddButton(panel, caption);

    /// <summary>
    /// CSAMPLE1: a plain button on a custom mod page: the game's compact button at the row's left edge, sized to its
    /// caption (<see cref="PageButtonLayout"/>); inside an inline block the buttons share one row, side by side.
    /// </summary>
    IRuntimeWidget? AddPageButton(IRuntimePanel panel, string caption) => AddButton(panel, caption);

    /// <summary>SETTINGS1 stage 2: repaint an options row's selection in place. Default no-op.</summary>
    void SetOptionStates(IRuntimeWidget widget, IReadOnlyList<bool> selected) { }

    /// <summary>
    /// SETTINGS1 stage 2: a caption and a read-only value that the handle rewrites through
    /// <see cref="IRuntimeWidget.StringValue"/>. Default: a fact row.
    /// </summary>
    IRuntimeWidget? AddInfo(IRuntimePanel panel, string text, string value) => AddFact(panel, text, value);

    /// <summary>SETTINGS1 stage 2: a thin horizontal rule row. Default: an empty label.</summary>
    IRuntimeWidget? AddDivider(IRuntimePanel panel) => AddLabel(panel, "");

    /// <summary>
    /// SETTINGS1 stage 2: rows added while on use the settings grid
    /// (<see cref="ModSettingsGrid"/>): the caption column, a fixed control column at the same x on
    /// every row, and a reset gutter. Default no-op.
    /// </summary>
    void SetSettingsGrid(IRuntimePanel panel, bool on) { }

    /// <summary>
    /// SETTINGS1 stage 2: help text under a row's caption, inside its caption column, muted and
    /// smaller; the row grows to hold it. Empty removes it. Default no-op.
    /// </summary>
    void SetRowHelp(IRuntimeWidget row, string help) { }

    /// <summary>
    /// TEXTAREA1: the most characters a text field or text area holds (typing and paste stop there;
    /// 0 = no limit). Default no-op.
    /// </summary>
    void SetInputLimit(IRuntimeWidget input, int characters) { }

    /// <summary>
    /// TEXTFILTER1: which characters a text field accepts while typing (all typing paths and paste).
    /// Default no-op.
    /// </summary>
    void SetInputFilter(IRuntimeWidget input, UiCharFilter filter) { }

    /// <summary>Settings organization review: draw the field's text masked (a password); a runtime without fields ignores it.</summary>
    void SetInputMasked(IRuntimeWidget input, bool masked) { }

    /// <summary>
    /// SETTINGS1 stage 2: a small square link in <paramref name="row"/>'s reset gutter. Default: an
    /// icon link on the next row, as the stage 1 reset link was drawn.
    /// </summary>
    IRuntimeWidget? AddGutterLink(IRuntimePanel panel, IRuntimeWidget row, string glyph, string tooltip) =>
        AddIconLink(panel, glyph, tooltip, attachToHeading: true);

    /// <summary>
    /// TEXTEXPAND1: a small square button in the bottom-right corner of <paramref name="row"/>'s text
    /// field. Default: the row's gutter link (<see cref="AddGutterLink"/>).
    /// </summary>
    IRuntimeWidget? AddCornerLink(IRuntimePanel panel, IRuntimeWidget row, string glyph, string tooltip) =>
        AddGutterLink(panel, row, glyph, tooltip);

    // ---- MENUKIT2: the game-look settings page (every member defaults to nothing) ------------------

    /// <summary>
    /// MENUKIT2: the settings grid's description column (the game's Options details panel). While on, a
    /// content area wide enough (<see cref="ModSettingsDescriptionLayout.HasColumn"/>) gets the column at
    /// its right and the grid rows draw no help; a narrower one draws each row's help as one line.
    /// </summary>
    void SetSettingsDescriptionMode(IRuntimePanel panel, bool on) { }

    /// <summary>MENUKIT2: what the description column says (<see cref="ModSettingsDescription"/>).</summary>
    void SetSettingsDescription(IRuntimePanel panel, string heading, string body, string notes, string tone) { }

    /// <summary>MENUKIT2: a grid row hovered or selected: its band and name light up (the game's row tint).</summary>
    void SetGridRowLit(IRuntimeWidget row, bool lit) { }

    /// <summary>
    /// MENUKIT2: the row's pointer enter/exit, as a widget whose <see cref="IRuntimeWidget.PointerEnterEvent"/>
    /// and <see cref="IRuntimeWidget.PointerExitEvent"/> can be wired to a slot; null when the runtime has none.
    /// </summary>
    IRuntimeWidget? GridRowPointer(IRuntimeWidget row) => null;

    /// <summary>MENUKIT2: a reset link's state (<see cref="SettingsResetState"/>): dim at the default, steel blue when changed.</summary>
    void SetResetMark(IRuntimeWidget link, SettingsResetState state) { }

    /// <summary>
    /// CONTROLS1 round 2: what the settings page drew, read from the live objects — per settings row its band
    /// and control images (sprite, color, type, rim, sprite states), per header row its buttons and fields —
    /// plus the game button art's donor and the skins still waiting for art. Null from a runtime that draws
    /// nothing (a test fake), so the readback says so instead of inventing a look.
    /// </summary>
    Dictionary<string, object?>? DescribeSettingsDrawn(IRuntimePanel panel) => null;

    /// <summary>MENUKIT2: a grid row's small chip at the end of its name (<c>Restart</c>, <c>Server</c>, <c>Awaiting server</c>); empty hides it.</summary>
    void SetRowChip(IRuntimeWidget row, string text) { }

    /// <summary>
    /// Settings organization live 2026-10-06: the caption <paramref name="row"/> draws in its name column, or null when the
    /// runtime cannot tell. A settings row whose control is drawn and whose caption is empty is a bug (readback
    /// <c>captionShown</c>).
    /// </summary>
    string? RowCaption(IRuntimeWidget row) => null;

    /// <summary>MENUKIT2 round E: a plain button drawn as the game's standard button, caption centered.</summary>
    void SetGameButton(IRuntimeWidget button) { }

    /// <summary>NAV1: the widget's rect in screen pixels (y up), or <see cref="ScreenRect.Unknown"/> (test fakes).</summary>
    ScreenRect WidgetRect(IRuntimeWidget widget) => ScreenRect.Unknown;

    /// <summary>
    /// NAV1: draw or remove the controller focus marker on <paramref name="widget"/>: the look the pointer's hover gives
    /// it (a grid row's band; a button's highlight). <paramref name="option"/> is a multi-select's option, else -1.
    /// </summary>
    void SetNavFocus(IRuntimeWidget widget, int option, bool on) { }

    /// <summary>NAV1: give the text field in <paramref name="widget"/> the keyboard, as a click on it does. False when it has none.</summary>
    bool FocusTextIn(IRuntimeWidget widget) => false;

    /// <summary>NAV1: scroll the list holding <paramref name="widget"/> by a stick's deflection (-1..1 per frame, positive up).</summary>
    void ScrollNear(IRuntimeWidget widget, float amount) { }

    /// <summary>
    /// INLINEAPPLY1: the art <paramref name="button"/> is drawn with now (<c>game-button</c>, <c>game-button-pending</c> or
    /// <c>kit</c>), or null when the runtime cannot tell.
    /// </summary>
    string? ButtonArt(IRuntimeWidget button) => null;

    /// <summary>
    /// NOPROGMSG1: a text field's muted hint while it is empty (empty clears it). It wraps in a text area and hides as soon
    /// as the field has text.
    /// </summary>
    void SetInputPlaceholder(IRuntimeWidget input, string text) { }

    /// <summary>MENUKIT2 round D: a destructive button (its caption in the danger tone).</summary>
    void SetDestructive(IRuntimeWidget button) { }

    /// <summary>MENUKIT2: the game's section rule (<c>Divider02_LeftToRight</c>) under a section heading row.</summary>
    void AddSectionRule(IRuntimeWidget headingRow) { }

    /// <summary>
    /// Tell <paramref name="panel"/> which of the shown panels it is — the <c>Rank</c>th of
    /// <c>Count</c> — and place it there now (jobs FUI20, FUI21).
    /// <see cref="PanelSpot.None"/> is a panel that is not shown. The runtime deactivates
    /// that overlay so a closed log cannot keep drawing at the center.
    ///
    /// <para>
    /// The offset itself is not passed in, because it is a function of the size the frame is given
    /// by the layout pass that may not have run yet — the runtime asks
    /// <see cref="PanelFlow.Offset"/> from inside that pass. Called once for the panel being shown
    /// before it is laid out, and again for every shown panel whenever the set changes, because
    /// where two panels go is a fact about there being two of them. Must never throw.
    /// </para>
    /// </summary>
    void PlacePanel(IRuntimePanel panel, PanelSpot spot);
    /// <summary>Place a transient panel at the top, keeping the center of the scene visible.</summary>
    void TopPanel(IRuntimePanel panel, bool top) { }

    void Show(IRuntimePanel panel);

    void Hide(IRuntimePanel panel);

    /// <summary>
    /// Attach the Bootstrap-owned UnityAction for <paramref name="widgetId"/> to this
    /// widget's matching UnityEvent. Tests record the id and do not touch Unity.
    /// </summary>
    bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget);

    void Unwire(ushort widgetId);

    void UnwireMany(IReadOnlyList<ushort> ids);

    /// <summary>
    /// Plugin unload: destroy the GameObject whose name equals <paramref name="rootName"/>
    /// exactly (Unity destroys children with it). Not a prefix match — a GUID that extends
    /// another plugin's GUID must not lose its root.
    /// </summary>
    int DestroyRoot(string rootName);

    /// <summary>
    /// Core Uninstall only: disable then destroy every DontDestroyOnLoad root whose name
    /// starts with <paramref name="prefix"/> (typically <see cref="UiOwnership.Prefix"/>).
    /// Plugin unload uses <see cref="DestroyRoot"/> instead. Returns how many roots were released.
    /// </summary>
    int DestroyByPrefix(string prefix);

    /// <summary>
    /// Unlock cursor + forward Escape. Nested Show is refcounted: snapshot lock/visible on
    /// each 0→1 capture and restore that snapshot on 1→0 (not the first capture of the process).
    ///
    /// <para>
    /// This is the half of "capture" that must last the whole time a panel is shown, and it is
    /// deliberately <b>not</b> routed by the pointer: a locked cursor cannot be moved to the panel
    /// in the first place. Only the gameplay input block follows the pointer
    /// (<see cref="UiCaptureMode"/>, <see cref="HoverRouter"/>).
    /// </para>
    /// </summary>
    void SetCapture(bool capture);

    /// <summary>
    /// This panel's frame in <b>screen pixels</b>, or <see cref="ScreenRect.Unknown"/> when it
    /// cannot be read (job FUI19). See <see cref="ScreenRect"/> for why screen pixels are the
    /// coordinate space and not the overlay canvas's own units. Must never throw.
    /// </summary>
    ScreenRect PanelRect(IRuntimePanel panel);

    /// <summary>
    /// The mouse position in <b>screen pixels</b>, or the explicit "no mouse" / "off the window"
    /// answers (job FUI19). Must never throw; a read that fails is
    /// <see cref="PointerSample.Unreadable"/>, which the router treats as fail-closed.
    /// </summary>
    PointerSample ReadPointer();

    /// <summary>
    /// KEYS8: true when the top UI raycast hit under the pointer is a Progenitor-owned object (any panel, overlay
    /// or HUD element the kit built, whether or not it takes input). False when there is no pointer, no event
    /// system or no hit. Must never throw. A raycast: call it on a press, not per frame.
    /// </summary>
    bool PointerOverOwnedUi() => false;

    /// <summary>
    /// Start watching <paramref name="site"/> and call <paramref name="onFrame"/> once per frame
    /// with what was found there (job FUI36). Dispose the return value to stop. Null means this
    /// runtime cannot watch that site — a server, a test fake that does not care, or a site whose
    /// driving system is absent — and the attachment then stays valid but never attaches.
    ///
    /// <para>
    /// For <see cref="VanillaUiSite.MapWarEventInfo"/> the seam is
    /// <c>SystemHooks.After&lt;MapMenuMapper&gt;</c>: the managed <c>SystemBase.Update</c>, which
    /// is the one entry point a mod may take on that system. Never Harmony on the mapper and
    /// never its Burst <c>LambdaJob_*</c> types.
    /// </para>
    ///
    /// <para>Defaulted so a runtime that does not host vanilla attachments — the null runtime,
    /// and every existing test fake — needs no change.</para>
    /// </summary>
    IDisposable? WatchSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame) => null;

    /// <summary>
    /// Create the mod-owned container for <paramref name="site"/>, placed per
    /// <paramref name="parent"/> and with <c>LayoutElement.ignoreLayout</c> when
    /// <paramref name="ignoreLayout"/>. Null when it could not be built. Must never throw and
    /// must never modify a vanilla object: the container is a <i>sibling</i> of the widget, so
    /// creating it changes no vanilla transform's children and destroying it restores nothing.
    /// </summary>
    IRuntimeWidget? BuildAttachContainer(
        VanillaUiSite site, string containerName, VanillaAttachParent parent, bool ignoreLayout) => null;

    /// <summary>
    /// Clone the site's donor widget into <paramref name="container"/> and set its text. The
    /// clone loses its <c>LocalizedText</c> (which re-applies its key's string on a locale change
    /// or a localization-version bump and would blank the row), has every inherited persistent
    /// <c>UnityEvent</c> call switched off, and has <c>raycastTarget = false</c> so it cannot eat
    /// a click meant for the map. Null when the clone came out without a usable text component,
    /// in which case the implementation has already destroyed it.
    /// </summary>
    IRuntimeWidget? CloneDonorRow(VanillaUiSite site, IRuntimeWidget container, string text) => null;

    /// <summary>
    /// P6: create the mod-owned container for a panel site at <paramref name="anchor"/>, using the
    /// panel instance the most recent sample resolved. Edge anchors parent it beside the panel
    /// (ignored by any vanilla layout group, positioned by <see cref="FitAttachContainer"/>); inline
    /// anchors put it inside the vanilla button group right after the anchor button; the tooltip
    /// anchor puts it into the tooltip's text column after the description. Null when it could not
    /// be built. Must never throw.
    /// </summary>
    IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName) => null;

    /// <summary>
    /// P6: clone the site's button donor into <paramref name="container"/> with
    /// <paramref name="caption"/>. The clone keeps the game's art and its hover and press transitions,
    /// loses every inherited persistent <c>UnityEvent</c> call and its icon, and exposes its
    /// <c>onClick</c> as <see cref="IRuntimeWidget.ClickEvent"/> for the Bootstrap slot. Null when
    /// no operable button came out.
    /// </summary>
    IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption) => null;

    /// <summary>P6: <see cref="ClonePanelButton(VanillaUiSite, IRuntimeWidget, string)"/> with the icon its face falls back to.</summary>
    IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption, VanillaButtonIcon icon) =>
        ClonePanelButton(site, container, caption);

    /// <summary>
    /// P6: place (edge anchors) or verify (inline and tooltip anchors) the container against the
    /// live panel, per <see cref="VanillaPanelPlacement"/>. Called after every build and every
    /// frame while attached; a <see cref="VanillaFit.Blocked"/> answer hides the container.
    /// </summary>
    VanillaFitResult FitAttachContainer(
        VanillaUiSite site, VanillaPanelAnchor anchor, IRuntimeWidget container, int rows, int buttons) =>
        VanillaFitResult.Unknown;
}

/// <summary>One cloned widget (or a fake). Handle property writes go through here.</summary>
internal interface IRuntimeWidget
{
    string Text { get; set; }

    bool Visible { get; set; }

    bool BoolValue { get; set; }

    float FloatValue { get; set; }

    string StringValue { get; set; }

    bool Alive { get; }

    object? ClickEvent { get; }

    object? FloatEvent { get; }

    object? BoolEvent { get; }

    object? TextEvent { get; }

    object? PointerEnterEvent { get; }

    object? PointerExitEvent { get; }

    /// <summary><c>left</c> or <c>centre</c>. Default center so existing fakes need no change.</summary>
    string Align { get => "centre"; }

    /// <summary>
    /// SETTINGS1 stage 2: false grays the widget and stops its selectables taking input. Default:
    /// always interactable, and the write is ignored, so existing fakes need no change.
    /// </summary>
    bool Interactable { get => true; set { } }

    /// <summary>Stop, disable, DestroyImmediate this object. Idempotent.</summary>
    void Release();
}

/// <summary>
/// A cell row on the Unity side (LIST1). <see cref="IRuntimeWidget.Text"/> is the row key
/// (so <c>widget_read</c> / <c>widget_click</c> address the row by it) and
/// <see cref="IRuntimeWidget.ClickEvent"/> is the whole-row click. Button cells are their own
/// widgets (<see cref="CellWidget"/>) with their own click event.
/// </summary>
internal interface IRuntimeCellRow : IRuntimeWidget
{
    /// <summary>The button cell <paramref name="key"/>'s widget (its click event), or null for a non-button cell.</summary>
    IRuntimeWidget? CellWidget(string key);

    /// <summary>
    /// RADIOCLICK1: give a row built without a whole-row click target (a plain, band or header row,
    /// <see cref="CellRowSpec.RowClickAtBuild"/>) one now, without a hover or press tint, and return its click event
    /// (<see cref="IRuntimeWidget.ClickEvent"/> afterwards). A row that already has one returns it unchanged. Before
    /// this, <c>OnRowClick</c> on a plain row bound a slot no pointer could ever reach.
    /// </summary>
    object? EnsureRowClick() => ClickEvent;

    /// <summary>Repaint one cell from its (already rewritten) spec: text, tone, visibility. Re-places this row's cells only.</summary>
    void ApplyCell(CellSpec spec);

    /// <summary>Give an image cell its sprite (a <c>UnityEngine.Sprite</c>; null clears to the placeholder fill).</summary>
    void SetImage(string key, object? sprite);

    void SetExpanded(bool expanded);

    void SetSelected(bool selected);

    /// <summary>P7b: draw tile <paramref name="tile"/> of a <see cref="CellRowSpec.TileStates"/> row as <paramref name="state"/>. Test fakes ignore it.</summary>
    void SetTileState(int tile, UiTileState state) { }

    /// <summary>Header: rewrite the captions so <paramref name="sortKey"/>'s column carries the indicator.</summary>
    void SetSorted(string? sortKey) { }

    /// <summary>INSTALLCOLS1: as <see cref="SetSorted(string?)"/>, with ▲ for an ascending column.</summary>
    void SetSorted(string? sortKey, bool ascending) => SetSorted(sortKey);

    /// <summary>Each visible cell's rect in screen pixels (<see cref="ScreenRect"/>), keyed by cell; empty when unreadable.</summary>
    IReadOnlyList<KeyValuePair<string, ScreenRect>> CellRects();

    /// <summary>The row's own rect in screen pixels.</summary>
    ScreenRect RowRect();

    /// <summary>
    /// INSTALLSIDES1: a <see cref="CellKind.ScrollText"/> cell's measured content height, its
    /// viewport height and the offset it is scrolled to. False when the cell is not one, or the
    /// runtime does not measure (test fakes).
    /// </summary>
    /// <summary>
    /// INSTALLSIDES2 live review 3: the images a cell draws (its own and its children's), for the
    /// readback. Empty when the runtime does not draw (test fakes).
    /// </summary>
    IReadOnlyList<Dictionary<string, object?>> CellImages(string key) => Array.Empty<Dictionary<string, object?>>();

    /// <summary>
    /// JOINLINKS1: the link of the linked cell <paramref name="key"/> under the pointer now (its index in
    /// <see cref="CellSpec.LinksNow"/>), or -1. Test fakes do not hit-test.
    /// </summary>
    int LinkAt(string key) => -1;

    bool TryScrollState(string key, out float contentHeight, out float viewportHeight, out float offset)
    {
        contentHeight = 0f;
        viewportHeight = 0f;
        offset = 0f;
        return false;
    }
}

/// <summary>Panel chrome. Same surface as a widget plus Show/Hide targeting.</summary>
internal interface IRuntimePanel : IRuntimeWidget
{
    /// <summary>
    /// Size and place this panel as the Mods workspace tab strip (one row of
    /// buttons in the frame top inset, no title). Default is a no-op so test
    /// fakes need no change.
    /// </summary>
    void ConfigureStrip() { }
}
