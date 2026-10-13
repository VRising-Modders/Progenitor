using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// SETTINGS1 (docs/mod-settings.md): a mod's settings drawn inline on its page, in sections.
///
/// <para><b>Stage 2 layout</b> (user redesign 2026-09-29). The settings header is one row: the
/// <b>Settings</b> heading, then — at its right — the search field (more than
/// <see cref="ModSettingsLayout.SearchThreshold"/> settings) and a small <b>Reset all</b> button.
/// Section headings are small and muted. Every setting is <b>one</b> row of the settings grid
/// (<see cref="ModSettingsGrid"/>): its display name with the help line under it in the caption column,
/// the control right-aligned in the fixed control column, and the reset link in the gutter while the
/// value differs from its default. The help line carries the modified marker
/// (<c>The default is &lt;default&gt;.</c>), a read-only or grayed reason, the scope marker, the pending
/// restart marker and a transient <c>Saved.</c> (<see cref="ModSettingsLayout.HelpText"/>).</para>
///
/// <para><b>Stage 2 options.</b> show-if hides a row and enable-if grays it in place, re-evaluated on
/// build and after every committed change on the page (<see cref="Reevaluate"/>); nothing is rebuilt,
/// exactly as search hides rows. A predicate that throws is logged once and the row is treated as
/// shown / enabled. needs-server-part grays a row with its reason and fallback until the mod's server
/// part is known to run (<see cref="ModSettingsLayout.ServerPartForSettings"/>). A grayed row refuses a
/// commit that still reaches it. needs-restart rows report <see cref="PendingRestartChanged"/> so the
/// framework status line can say <c>Pending restart</c>.</para>
///
/// <para>Writes go through <see cref="SettingsCatalog.Submit"/> (the settings panel's own path), so
/// validation, bounds, a registered binding's policy and the server-synced refusal are unchanged. A
/// synced entry opted into admin editing keeps its field, <b>Apply</b> button and status
/// (<see cref="ServerSettings.Request(string, string, string, Action{ServerSettingResult})"/>).</para>
/// </summary>
internal sealed partial class ModSettingsView
{
    // Readback row keys (docs/mod-settings.md, docs/mods-menu.md).
    internal const string RowSection = "page-section";
    internal const string RowSetting = "page-setting";
    internal const string RowSettingNote = "page-setting-note";
    internal const string RowSettingReset = "page-setting-reset";
    internal const string RowSearch = "page-settings-search";
    internal const string RowResetAll = "page-settings-reset-all";

    /// <summary>CONTROLS1 round 2: the live drawn look of every settings control (<see cref="UiSettingsControls.SettingsDrawn"/>).</summary>
    internal const string RowDrawn = "page-settings-drawn";
    internal const string RowDivider = "page-divider";

    /// <summary>The settings header's cell-row key, and its cells.</summary>
    internal const string HeaderKey = "page-settings-head";
    internal const string CellTitle = "title";
    internal const string CellSearch = "search";
    internal const string CellResetAll = "reset-all";

    /// <summary>A section heading's cell-row key prefix; the row is <c>page-section-&lt;n&gt;</c>.</summary>
    internal const string SectionKeyPrefix = "page-section-";

    /// <summary>KEYS1: the Keys section heading's button that opens this mod's section of the Keybinds tab.</summary>
    internal const string CellOpenKeybinds = "open-keybinds";

    internal const string OpenKeybindsCaption = "Open in Keybinds";

    internal const string SearchCaption = "Search settings";
    internal const string ResetAllCaption = "Reset all";
    /// <summary>The reset link's glyph: Latin-1, the pair of the store link's <c>»</c>, so the game font has it.</summary>
    internal const string ResetGlyph = "«";
    internal const string ResetTooltipPrefix = "Reset to ";
    internal const string NoMatches = "No settings match the search.";
    internal const string SavedNote = "Saved.";
    internal const string AdminNote = "Server setting; changes need administrator permission.";
    internal const string WaitingNote = "Waiting for the server…";
    internal const string ServerSavedNote = "Saved by the server.";
    internal const string DisabledReason = "Not available right now.";

    /// <summary>The caption of a registered action's button; the action's name is on the left.</summary>
    internal const string RunCaption = "Run";

    /// <summary>What a read-only value is prefixed with in the control column (the font has no lock glyph).</summary>
    internal const string LockedPrefix = "Locked · ";

    /// <summary>Decided 2026-10-06: what an empty read-only value reads, drawn muted (<see cref="IsNotSetShown"/>).</summary>
    internal const string NotSetValue = "Not set";

    /// <summary><paramref name="value"/>, or <see cref="NotSetValue"/> when it is empty or whitespace.</summary>
    internal static string OrNotSet(string? value) => string.IsNullOrWhiteSpace(value) ? NotSetValue : value!;

    /// <summary>
    /// True when a read-only value's shown text is <see cref="NotSetValue"/>, bare or after a lock word
    /// (<c>Locked · Not set</c>, <c>Admin only · Not set</c>): the runtime draws it muted.
    /// </summary>
    internal static bool IsNotSetShown(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return string.Equals(text, NotSetValue, StringComparison.Ordinal)
            || text!.EndsWith(" · " + NotSetValue, StringComparison.Ordinal);
    }

    /// <summary>Header heading size, and a section heading's.</summary>
    internal const float HeaderFontSize = 22f;
    internal const float SectionFontSize = 15f;
    internal const float SectionRowHeight = 42f;
    internal const float SearchCellWidth = 220f;

    /// <summary>What a read-only Server-wide value is prefixed with for a player who is not an admin.</summary>
    internal const string AdminOnlyPrefix = SettingAvailability.AdminOnlyNote + " · ";

    private readonly string _pluginId;
    private readonly IReadOnlyList<ModSettingsSection> _original;
    // Settings scopes (step 4): what the page draws — the sections regrouped under Your settings and
    // Server settings (ModSettingsScopes.Regroup), flattened in draw order.
    private readonly IReadOnlyList<ModSettingsSection> _sections;
    private readonly List<ScopeDrawGroup> _groups;
    private readonly List<(ScopeDrawGroup Group, IUiHandle Heading, IUiHandle? Line)> _scopeHeadings = new();
    private readonly Func<ServerPartState>? _serverPart;
    private readonly Func<SettingsScopeContext>? _scopeContext;
    private SettingsScopeContext _context;
    private string _builtKey = "";
    private string _query;
    private readonly List<ItemBinding> _bindings = new();
    private readonly List<(ModSettingsSection Section, IUiHandle Heading)> _headings = new();
    private readonly List<IDisposable> _requests = new();
    private IUiLabel? _noMatches;
    private bool _lastPending;
    private IUiPanel? _panel;

    internal ModSettingsView(
        string pluginId, IReadOnlyList<ModSettingsSection> sections, string? query, Func<ServerPartState>? serverPart = null,
        Func<SettingsScopeContext>? scopeContext = null)
    {
        _pluginId = pluginId ?? "";
        _original = sections ?? Array.Empty<ModSettingsSection>();
        _groups = ModSettingsScopes.Regroup(_original);
        var drawn = new List<ModSettingsSection>();
        foreach (var group in _groups) drawn.AddRange(group.Sections);
        _sections = drawn;
        _query = (query ?? "").Trim();
        _serverPart = serverPart;
        _scopeContext = scopeContext;
        _context = ReadContext();
        _lastPending = AnyPendingRestart();
    }

    internal string PluginId => _pluginId;

    /// <summary>Every section the page was built with, before the search filter and the scope regrouping.</summary>
    internal IReadOnlyList<ModSettingsSection> Sections => _original;

    /// <summary>Settings scopes: the sections as drawn, under Your settings then Server settings.</summary>
    internal IReadOnlyList<ScopeDrawGroup> ScopeGroups => _groups;

    /// <summary>Settings scopes: the context the page last evaluated with.</summary>
    internal SettingsScopeContext Context => _context;

    /// <summary>
    /// Settings scopes: true when the joined server's answer now changes which control a row is drawn
    /// with (admin or not, joined or not): the owner rebuilds the page. A grayed reason alone changes
    /// in place (<see cref="Reevaluate"/>).
    /// </summary>
    internal bool NeedsRebuildForScope()
    {
        string now = ReadContext().BuildKey;
        if (_builtKey.Length > 0 && !string.Equals(now, _builtKey, StringComparison.Ordinal) && HasServerWideRows()) return true;
        // An admin row drawn read-only for a non-admin (or editable for an admin) whose player's standing changed.
        if (_builtKey.Length > 0 && AdminViewChanged()) return true;
        // The joined server's lock list changed: a server-wide row is drawn read-only, or editable again.
        return _builtKey.Length > 0 && !string.Equals(ServerSettingsLock.ClientKey(), _builtLockKey, StringComparison.Ordinal) && HasServerWideRows();
    }

    private string _builtLockKey = "";

    private bool HasServerWideRows()
    {
        foreach (var group in _groups) if (group.Scope == SettingScope.ServerWide) return true;
        return false;
    }

    private SettingsScopeContext ReadContext()
    {
        if (_scopeContext != null)
        {
            try { return _scopeContext() ?? SettingsScopeContext.FromServerPart(CurrentServerPart()); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: settings scope context threw: " + e.Message);
            }
        }

        return SettingsScopeContext.FromServerPart(CurrentServerPart());
    }

    /// <summary>Called with the new search text after the filter has been applied in place, so the owner can remember it.</summary>
    internal Action<string>? Search { get; set; }

    /// <summary>Called when <b>Reset all</b> is pressed and there is something to reset.</summary>
    internal Action? ResetAll { get; set; }

    /// <summary>
    /// SETTINGS1 stage 2: called with the new answer when a commit changes whether any needs-restart
    /// row differs from its value at load (the framework status line's <c>Pending restart</c>).
    /// </summary>
    internal Action<bool>? PendingRestartChanged { get; set; }

    /// <summary>Round D: a Back button on the action row (the raw settings page's way back to its mod); null draws none.</summary>
    internal Action? Back { get; set; }

    internal const string CellBack = "back";

    internal const string BackCaption = "Back to mod";

    /// <summary>KEYS5: the Back button's caption (<see cref="BackCaption"/>, or Back to Configuration).</summary>
    internal string BackText { get; set; } = BackCaption;

    /// <summary>KEYS1: called with the mod's id by the Keys section's <b>Open in Keybinds</b> button; null draws no button.</summary>
    internal Action<string>? OpenKeybinds { get; set; }

    // ---- MENUKIT2: the description column's model (hover, else selection, else the first row) -------

    private string? _hoveredId;
    private string? _selectedId;

    // OPTIONHOVER1: the option under the pointer (a dropdown's open list, a radio line) and the row it belongs to.
    private string? _optionItemId;
    private UiChoiceOption _option;

    /// <summary>Called with the new description when the hovered or selected row changes, or a note on it does.</summary>
    internal Action<ModSettingsDescription>? DescriptionChanged { get; set; }

    /// <summary>What the column says with nothing to describe (the page owner's title and one-liner).</summary>
    internal string EmptyHeading { get; set; } = ModPageCopy.SettingsHeading;

    internal string EmptyBody { get; set; } = "";

    internal string? HoveredId => _hoveredId;

    internal string? SelectedId => _selectedId;

    /// <summary>The pointer entered (<paramref name="itemId"/>) or left (null) a row.</summary>
    internal void Hover(string? itemId) => SetFocus(ref _hoveredId, itemId);

    /// <summary>A row was selected (clicked, or reached by keyboard); null clears the selection.</summary>
    internal void Select(string? itemId) => SetFocus(ref _selectedId, itemId);

    /// <summary>NAV1: the controller's focus came to (or left) a row: it is the selected row while focused.</summary>
    private void NavFocus(string itemId, bool on)
    {
        if (on) Select(itemId);
        else if (string.Equals(_selectedId, itemId, StringComparison.Ordinal)) Select(null);
    }

    /// <summary>NAV1: X on a row: what its reset arrow does, when the arrow would be live.</summary>
    private bool NavReset(ItemBinding binding, SettingsRow row)
    {
        if (ResetState(binding) != SettingsResetState.Changed) return false;
        ResetRow(row);
        return true;
    }

    /// <summary>OPTIONHOVER1: the row whose option the pointer is over now, or null.</summary>
    internal string? HoveredOptionItemId => _optionItemId;

    /// <summary>OPTIONHOVER1: the option the pointer is over now (meaningful while <see cref="HoveredOptionItemId"/> is set).</summary>
    internal UiChoiceOption HoveredOption => _option;

    /// <summary>
    /// OPTIONHOVER1: the pointer is over <paramref name="option"/> of row <paramref name="itemId"/> (null: over no option).
    /// While it is, the column describes that option under the setting's name (<see cref="ModSettingsDescription.ForOption"/>).
    /// </summary>
    internal void HoverOption(string? itemId, UiChoiceOption? option)
    {
        string? next = string.IsNullOrEmpty(itemId) || option == null || Find(itemId!) == null ? null : itemId;
        var value = next == null ? default : option!.Value;
        if (string.Equals(_optionItemId, next, StringComparison.Ordinal) && _option.Equals(value)
            && string.Equals(_option.Label, value.Label, StringComparison.Ordinal)) return;
        _optionItemId = next;
        _option = value;
        NotifyDescription();
    }

    /// <summary>A choice control's option hover, wired per row at build: an index of its options, or -1 when it left them.</summary>
    private void OptionHovered(string itemId, IUiOptionHover control, int index)
    {
        var options = control.Options;
        if (index >= 0 && index < options.Count) HoverOption(itemId, options[index]);
        else if (string.Equals(_optionItemId, itemId, StringComparison.Ordinal)) HoverOption(null, null);
    }

    private void SetFocus(ref string? slot, string? itemId)
    {
        string? next = string.IsNullOrEmpty(itemId) || Find(itemId!) == null ? null : itemId;
        if (string.Equals(slot, next, StringComparison.Ordinal)) return;
        string? before = slot;
        slot = next;
        Relight(before);
        Relight(next);
        NotifyDescription();
    }

    /// <summary>A row is lit while it is hovered or selected (the game's row band and bright name).</summary>
    private void Relight(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId) || Find(itemId!) is not { Main: { IsDisposed: false } main } binding) return;
        bool lit = string.Equals(itemId, _hoveredId, StringComparison.Ordinal) || string.Equals(itemId, _selectedId, StringComparison.Ordinal);
        // LISTING1: a counter or status line (Room left, Length, Countdown now, Status) keeps its help in the column but
        // never gets the selectable highlight.
        main.SetRowLit(lit && ModSettingsLayout.TakesHighlight(binding.Item.Kind, binding.Control));
    }

    /// <summary>The pointer entered or left a row (the runtime's row hover, wired per row at build).</summary>
    /// <summary>LISTING1: the section heading under the pointer (its title), or null.</summary>
    private string? _hoveredSection;

    private void SectionHovered(string title, bool entered)
    {
        if (entered) _hoveredSection = title;
        else if (string.Equals(_hoveredSection, title, StringComparison.Ordinal)) _hoveredSection = null;
        NotifyDescription();
    }

    private void RowHovered(string itemId, bool entered)
    {
        if (entered) _hoveredSection = null;
        if (entered) Hover(itemId);
        else if (string.Equals(_hoveredId, itemId, StringComparison.Ordinal)) Hover(null);
    }

    /// <summary>Wire a drawn row's hover to the column (a no-op on a runtime without row pointers).</summary>
    private void WatchHover(IUiPanel panel, ItemBinding binding)
    {
        if (binding.Main == null) return;
        string id = binding.Item.Id;
        // NAV1: a controller's focus on the row is the row's selection (the band, the description column); X resets it,
        // so the reset arrow is not a stop of its own.
        var resetRow = binding.Reset != null && binding.Item.Row is { } navRow ? navRow : null;
        UiNav.Hook(binding.Main, new NavRowHook(
            on => NavFocus(id, on),
            resetRow == null ? null : () => NavReset(binding, resetRow)));
        if (binding.Reset != null) UiNav.Skip(binding.Reset);
        try { _requests.Add(panel.OnRowHover(binding.Main, entered => RowHovered(id, entered))); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings row hover threw: " + e.Message); }
        // OPTIONHOVER1: a dropdown's or radio list's option under the pointer describes itself in the column.
        if (binding.Main is IUiOptionHover choice)
        {
            try { _requests.Add(choice.OnOptionHover(index => OptionHovered(id, choice, index))); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings option hover threw: " + e.Message); }
        }
    }

    private void NotifyDescription()
    {
        ShowDescriptionNow();
        var handler = DescriptionChanged;
        if (handler == null) return;
        try { handler(Description()); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings description callback threw: " + e.Message); }
    }

    /// <summary>
    /// The header row's own title (<b>Settings</b>). MODPAGE2: empty on a mod page, which draws its Settings section heading
    /// above the row, so the row keeps only the search and Reset all.
    /// </summary>
    internal string HeaderTitle { get; set; } = ModPageCopy.SettingsHeading;

    /// <summary>MODPAGE2: write this view's description into the column again (the page's details rows hand it back on leave).</summary>
    internal void ShowDescriptionAgain() => ShowDescriptionNow();

    /// <summary>Write the current description into the page's column.</summary>
    private void ShowDescriptionNow()
    {
        if (_panel == null) return;
        try { _panel.ShowDescription(Description()); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings description threw: " + e.Message); }
    }

    /// <summary>The first visible setting, button or info line (the column's default subject).</summary>
    internal string? FirstVisibleId
    {
        get
        {
            for (int i = 0; i < _bindings.Count; i++)
                if (_bindings[i].Visible && _bindings[i].Item.Kind != ModSettingsItemKind.Divider) return _bindings[i].Item.Id;
            return null;
        }
    }

    /// <summary>
    /// MENUKIT2: the description column now: the hovered row, else the selected one, else the first visible
    /// one; a hidden row is skipped. The same sentences the row's help line carries, split into the help and
    /// its notes (<see cref="ModSettingsDescription"/>).
    /// </summary>
    internal ModSettingsDescription Description()
    {
        // OPTIONHOVER1: an option under the pointer comes first: the setting's name, the option and its explanation, then
        // the setting's own notes ("Applies at the next restart.").
        string? optionRow = VisibleOrNull(_optionItemId);
        if (optionRow != null)
        {
            var setting = DescriptionOf(optionRow, ModSettingsDescription.SourceOption);
            if (setting.RowId.Length > 0) return ModSettingsDescription.ForOption(setting, _option);
        }

        // LISTING1: a hovered section heading describes its section.
        if (_hoveredSection != null && ModSettingsSectionHelp.For(_hoveredSection) is { Length: > 0 } sectionHelp)
            return ModSettingsDescription.Empty(_hoveredSection, sectionHelp);

        string? hovered = VisibleOrNull(_hoveredId);
        string? selected = VisibleOrNull(_selectedId);
        var (id, source) = ModSettingsDescription.Pick(hovered, selected, FirstVisibleId);
        return DescriptionOf(id, source);
    }

    /// <summary>The column for item <paramref name="id"/> as <paramref name="source"/> found it (empty for an unknown id).</summary>
    private ModSettingsDescription DescriptionOf(string id, string source)
    {
        var binding = id.Length == 0 ? null : Find(id);
        if (binding == null) return ModSettingsDescription.Empty(EmptyHeading, EmptyBody);
        var item = binding.Item;
        if (item.Kind == ModSettingsItemKind.Setting && item.Row != null)
            return ModSettingsDescription.For(item.Row, item.Options, binding.Control, binding.Why, binding.Status, source,
                scopeWhy: binding.AdminOnly || binding.ServerPartGreyed);
        string label = !string.IsNullOrWhiteSpace(item.Options.Label) ? item.Options.Label! : item.Label;
        string? help = item.Options.Help;
        if (item.Options.HelpNow is { } helpNow)
        {
            try { help = helpNow(); }
            catch { /* the static help */ }
        }

        return ModSettingsDescription.ForExtra(item.Id, label, help, binding.Why, binding.Status, source);
    }

    /// <summary>MENUKIT2: a row's marks (reset arrow state, restart chip, grayed, conflict tone), or null for an unknown id.</summary>
    internal ModSettingsRowMarks? MarksOf(string itemId)
    {
        var binding = Find(itemId);
        if (binding == null) return null;
        bool lit = string.Equals(itemId, _hoveredId, StringComparison.Ordinal) || string.Equals(itemId, _selectedId, StringComparison.Ordinal);
        return ModSettingsRowMarks.For(binding.Item.Row, binding.Item.Options, binding.Enabled, lit);
    }

    private string? VisibleOrNull(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var binding = Find(id!);
        return binding != null && binding.Visible ? id : null;
    }


    /// <summary>
    /// KEYS1: the heading of the section that holds this mod's keys — the automatic Keys section, else
    /// the first builder section with a keybind row — or null. The Keybinds tab's <b>Open mod page</b>
    /// scrolls here.
    /// </summary>
    internal IUiHandle? KeysHeading { get; private set; }

    /// <summary>The last row drawn (a jump reveals from a heading down to here).</summary>
    internal IUiHandle? LastHandle => _bindings.Count > 0 ? _bindings[_bindings.Count - 1].Main : null;

    /// <summary>The settings header row (heading, search, Reset all), once built.</summary>
    internal IUiCellRow? HeaderRow { get; private set; }

    /// <summary>Settings, buttons and info lines: what the search threshold counts.</summary>
    internal int TotalRows => ModSettingsLayout.ItemCount(_sections);

    internal bool SearchShown => ModSettingsLayout.ShowSearch(TotalRows);

    internal string Query => SearchShown ? _query : "";

    /// <summary>The sections after the search filter (setting rows only).</summary>
    internal IReadOnlyList<ModSettingsSection> Shown => ModSettingsLayout.Filter(_sections, Query);

    /// <summary>Items drawn and visible now (search and show-if applied), dividers excluded.</summary>
    internal int ShownRows
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _bindings.Count; i++)
                if (_bindings[i].Item.Kind != ModSettingsItemKind.Divider && _bindings[i].Visible) n++;
            return n;
        }
    }

    /// <summary>Item ids in draw order. Tests.</summary>
    internal IReadOnlyList<string> RowIds
    {
        get
        {
            var ids = new List<string>(_bindings.Count);
            for (int i = 0; i < _bindings.Count; i++) ids.Add(_bindings[i].Item.Id);
            return ids;
        }
    }

    /// <summary>Ids of the items show-if hides now. The readback lists them on <see cref="RowResetAll"/>, since a hidden row has no row to read.</summary>
    internal IReadOnlyList<string> HiddenByShowIf
    {
        get
        {
            var ids = new List<string>();
            for (int i = 0; i < _bindings.Count; i++) if (!_bindings[i].ShownByIf) ids.Add(_bindings[i].Item.Id);
            return ids;
        }
    }

    /// <summary>True when any needs-restart row differs from its value at load.</summary>
    internal bool AnyPendingRestart()
    {
        for (int i = 0; i < _sections.Count; i++)
            for (int j = 0; j < _sections[i].Items.Count; j++)
            {
                var item = _sections[i].Items[j];
                if (item.Row != null && ModSettingsLayout.IsPendingRestart(item.Row, item.Options)) return true;
            }

        return false;
    }

    /// <summary>
    /// Draw the settings into <paramref name="panel"/>. <paramref name="add"/> registers a row's
    /// readback key; a <see cref="Func{TResult}"/> value is evaluated when the row is read, so a
    /// reader sees the value after a commit rather than the one at build time.
    /// </summary>
    internal void Build(IUiPanel panel, Action<IUiHandle, string, object?> add)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        add ??= (_, _, _) => { };
        _panel = panel;
        Drawn = this;
        // MENUKIT2: the art is whatever the idle preparation has ready (SettingsArtDonors.IdleTick); a part
        // not ready yet draws the kit's look. Nothing is scanned or copied on this path.

        // Every row writes its help line, and each write re-lays the panel out; one batch makes
        // the build one layout pass instead of one per row.
        UiLayoutBatch.Begin();
        try { BuildRows(panel, add); }
        finally { UiLayoutBatch.End(); }
    }

    /// <summary>
    /// Settings organization stage 3: the Configuration tab draws its mod-manager settings with this view, the mod
    /// page's row kit. Embedded, the view draws no settings header (search, Reset all) and no scope heading, and each
    /// section title is the game's section header with its red rule (as the tab's other sections), keyed
    /// <c>config-section-&lt;n&gt;</c>.
    /// </summary>
    internal bool Embedded { get; set; }

    /// <summary>
    /// The Server tab (requested 2026-10-06, "why do all the settings under server have server chips?"): every row there is a
    /// server setting, so the <c>Server</c> chip says nothing and is left off. <c>Restart</c>, <c>Locked</c> and
    /// <c>Awaiting server</c> still show. Tabs that mix personal and server rows keep the chip.
    /// </summary>
    internal bool OmitServerChip { get; set; }

    /// <summary>The chip a row of this view shows: <paramref name="chip"/>, less the <c>Server</c> chip when <see cref="OmitServerChip"/>.</summary>
    internal static string ChipFor(string chip, bool omitServerChip) =>
        omitServerChip && string.Equals(chip, ModSettingsRowMarks.ServerChip, StringComparison.Ordinal) ? "" : chip ?? "";

    /// <summary>The section heading key prefix of an embedded view (<see cref="Embedded"/>).</summary>
    internal const string EmbeddedSectionKeyPrefix = "config-section-";

    /// <summary>Settings organization: the <b>Server settings</b> heading as drawn, or null (a link lands there).</summary>
    internal IUiHandle? ServerHeading
    {
        get
        {
            foreach (var (group, heading, _) in _scopeHeadings)
                if (group.Scope == SettingScope.ServerWide && heading is { IsDisposed: false }) return heading;
            return null;
        }
    }

    /// <summary>
    /// Three homes: the heading drawn for the group <paramref name="title"/> (a section's subheading or drawer header,
    /// matched by its title or its author title; or a scope heading by its text, such as <b>On this server</b>), or null.
    /// </summary>
    internal IUiHandle? HeadingFor(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        foreach (var (section, heading) in _headings)
        {
            if (heading == null || heading.IsDisposed) continue;
            if (string.Equals(section.Title, title, StringComparison.OrdinalIgnoreCase)
                || string.Equals(section.SourceTitle, title, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GroupNavCaption(section), title, StringComparison.OrdinalIgnoreCase))
                return heading;
        }

        foreach (var (group, heading, _) in _scopeHeadings)
            if (heading is { IsDisposed: false } && string.Equals(ScopeHeadingText(group), title, StringComparison.OrdinalIgnoreCase))
                return heading;
        return null;
    }

    /// <summary>
    /// Three homes: the scope heading drawn for Server settings when set (Progenitor's page: <b>On this server</b>),
    /// and the words added after its <c>Applies to everyone on …</c> line. Mod authors' pages leave both empty.
    /// </summary>
    internal string ServerScopeHeading { get; set; } = "";

    internal string ServerScopeLineSuffix { get; set; } = "";

    private string ScopeHeadingText(ScopeDrawGroup group) =>
        group.Scope == SettingScope.ServerWide && ServerScopeHeading.Length > 0 ? ServerScopeHeading : group.Heading;

    private string ScopeLineText() => _context.ServerLine + ServerScopeLineSuffix;

    /// <summary>The left-list caption of a section (the Server rules group is listed as <b>Server rules</b>).</summary>
    internal static string GroupNavCaption(ModSettingsSection section) =>
        ProgenitorPage.NavCaption(section.Title.Length > 0 ? section.Title : section.SourceTitle);

    private void BuildRows(IUiPanel panel, Action<IUiHandle, string, object?> add)
    {
        panel.SettingsGrid(true);
        panel.SettingsDescriptionMode(true);
        try
        {
            if (!Embedded) BuildHeader(panel, add);

            _noMatches = panel.Note(NoMatches);
            _noMatches.Visible = false;

            _context = ReadContext();
            _builtKey = _context.BuildKey;
            _builtLockKey = ServerSettingsLock.ClientKey();
            int i = 0;
            for (int g = 0; g < _groups.Count; g++)
            {
                var group = _groups[g];
                if (!Embedded) BuildScopeHeading(panel, g, group, add);
                foreach (var section in group.Sections)
                {
                    BuildSection(panel, i, section, add);
                    i++;
                }
            }
        }
        finally
        {
            panel.SettingsGrid(false);
        }

        Reevaluate(notify: false);
        ShowDescriptionNow();
    }

    /// <summary>
    /// Settings scopes: a scope section's heading — <b>Your settings</b> or <b>Server settings</b> as the
    /// game's section header with its red rule — and, under Server settings, <c>Applies to everyone on …</c>.
    /// </summary>
    private void BuildScopeHeading(IUiPanel panel, int index, ScopeDrawGroup group, Action<IUiHandle, string, object?> add)
    {
        try
        {
            var heading = panel.CellRow(ModSettingsScopes.HeadingRow(index, ScopeHeadingText(group)));
            heading.SectionRule();
            IUiHandle? lineRow = null;
            if (group.Scope == SettingScope.ServerWide)
                lineRow = panel.CellRow(ModSettingsScopes.LineRow(index, ScopeLineText()));
            _scopeHeadings.Add((group, heading, lineRow));
            add(heading, ModSettingsScopes.RowScopeSection, (Func<object?>)(() => DescribeScope(group)));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings scope heading threw: " + e.Message);
        }
    }

    private Dictionary<string, object?> DescribeScope(ScopeDrawGroup group)
    {
        int rows = 0, shown = 0;
        var titles = new List<string>();
        foreach (var section in group.Sections)
        {
            titles.Add(section.Title);
            rows += section.Rows.Count;
        }

        foreach (var binding in _bindings)
            if (binding.Section.Scope == group.Scope && binding.Visible && binding.Item.Kind != ModSettingsItemKind.Divider) shown++;
        var context = ReadContext();
        var d = new Dictionary<string, object?>
        {
            ["scope"] = SettingScopes.Word(group.Scope),
            ["heading"] = ScopeHeadingText(group),
            ["line"] = group.Scope == SettingScope.ServerWide ? context.ServerLine + ServerScopeLineSuffix : "",
            ["sections"] = titles,
            ["rows"] = rows,
            ["shown"] = shown,
            ["canEdit"] = group.Scope != SettingScope.ServerWide || context.CanEditServerWide,
            ["context"] = context.Readback(),
        };
        return d;
    }

    private void BuildSection(IUiPanel panel, int i, ModSettingsSection section, Action<IUiHandle, string, object?> add)
    {
        {
            {
                IUiHandle? heading = null;
                bool keys = IsKeysSection(section) && KeysHeading == null;
                if (section.Title.Length > 0)
                {
                    try
                    {
                        // Settings scopes: an author section is a subheading inside its scope section. Embedded (the
                        // Configuration tab), it is the game's section header with its red rule.
                        IUiCellRow row;
                        int settings = SettingsDrawers.CountOf(section);
                        if (DrawerScope.Length > 0 && SettingsDrawers.IsDrawer(settings))
                        {
                            // Three homes: a group of more than four settings is a drawer, open or closed as remembered.
                            row = panel.CellRow(ModSettingsScopes.DrawerRow(section.Title, settings, keys && OpenKeybinds != null, GroupNavCaption(section)));
                            string key = SettingsDrawers.KeyOf(DrawerScope, GroupNavCaption(section));
                            var drawer = new DrawerState(section, row, key, SettingsDrawers.IsOpen(key));
                            _drawers.Add(drawer);
                            row.SetExpanded(drawer.Open);
                            row.OnRowClick(() => ToggleDrawer(drawer));
                            row.OnClick(ModSettingsScopes.CellDrawerChevron, () => ToggleDrawer(drawer));
                        }
                        else if (DrawerScope.Length > 0)
                        {
                            // Three homes: four settings or fewer is a plain gold small-caps subheading, on every home.
                            row = panel.CellRow(ModSettingsScopes.PlainSubheadingRow(i, section.Title, keys && OpenKeybinds != null));
                        }
                        else if (Embedded)
                        {
                            var spec = ModSettingsScopes.HeadingRow(i, section.Title);
                            row = panel.CellRow(new CellRowSpec(EmbeddedSectionKeyPrefix + i.ToString(CultureInfo.InvariantCulture),
                                spec.Cells, height: spec.ExplicitHeight, rightInset: spec.RightInset, plain: true));
                            row.SectionRule();
                        }
                        else
                        {
                            row = panel.CellRow(ModSettingsScopes.SubheadingRow(i, section.Title, keys && OpenKeybinds != null));
                        }

                        heading = row;
                        if (keys && OpenKeybinds != null)
                        {
                            string id = _pluginId;
                            row.OnClick(CellOpenKeybinds, () => OpenKeybinds?.Invoke(id));
                        }

                        if (keys) KeysHeading = row;
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning("Ui: settings section heading threw: " + e.Message);
                    }

                    if (heading != null)
                    {
                        _headings.Add((section, heading));
                        var described = section;
                        add(heading, RowSection, (Func<object?>)(() => DescribeSection(described)));
                        // LISTING1: a heading with its own help describes its section while hovered.
                        string title = section.Title;
                        if (ModSettingsSectionHelp.For(title).Length > 0)
                        {
                            try { _requests.Add(panel.OnRowHover(heading, entered => SectionHovered(title, entered))); }
                            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings heading hover threw: " + e.Message); }
                        }
                    }
                }

                for (int j = 0; j < section.Items.Count; j++)
                {
                    var item = section.Items[j];
                    try { AddItem(panel, section, item, add); }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning("Ui: settings row " + item.Id + " threw: " + e.Message);
                    }
                }
            }
        }
    }

    // ---- Three homes: drawers -------------------------------------------------------------------------

    /// <summary>
    /// Three homes: the drawer key scope of this view (<see cref="SettingsDrawers"/>: <c>preferences</c>, <c>server</c>,
    /// <c>progenitor</c>). Set, a group of more than four settings is a drawer and every other group a gold small-caps
    /// subheading. Empty (mod authors' pages): no drawers, the headings as before.
    /// </summary>
    internal string DrawerScope { get; set; } = "";

    private sealed class DrawerState
    {
        internal DrawerState(ModSettingsSection section, IUiCellRow row, string key, bool open)
        {
            Section = section;
            Row = row;
            Key = key;
            Open = open;
        }

        internal ModSettingsSection Section { get; }
        internal IUiCellRow Row { get; }
        internal string Key { get; }
        internal bool Open { get; set; }
        internal int Shown { get; set; }
    }

    private readonly List<DrawerState> _drawers = new();

    /// <summary>Called after a drawer opened or closed (the owner refreshes the left list's highlight).</summary>
    internal Action<string, bool>? DrawerToggled { get; set; }

    private DrawerState? FindDrawer(string? group)
    {
        if (string.IsNullOrEmpty(group)) return null;
        string slug = SectionNav.Slug(group);
        foreach (var d in _drawers)
            if (string.Equals(d.Section.Title, group, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GroupNavCaption(d.Section), group, StringComparison.OrdinalIgnoreCase)
                || string.Equals(SectionNav.Slug(GroupNavCaption(d.Section)), slug, StringComparison.Ordinal)
                || string.Equals(d.Key, group, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    /// <summary>True when <paramref name="group"/> is drawn as a drawer.</summary>
    internal bool IsDrawer(string? group) => FindDrawer(group) != null;

    /// <summary>The drawer's state: true open, false closed, null when the group is not a drawer.</summary>
    internal bool? DrawerOpen(string? group) => FindDrawer(group)?.Open;

    /// <summary>
    /// Open or close a drawer in place (its rows are shown or hidden; nothing is rebuilt) and remember it. False when the
    /// group is not a drawer.
    /// </summary>
    internal bool SetDrawerOpen(string? group, bool open)
    {
        var drawer = FindDrawer(group);
        if (drawer == null) return false;
        SetDrawer(drawer, open);
        return true;
    }

    private void ToggleDrawer(DrawerState drawer) => SetDrawer(drawer, !drawer.Open);

    private void SetDrawer(DrawerState drawer, bool open)
    {
        bool changed = drawer.Open != open;
        drawer.Open = open;
        SettingsDrawers.SetOpen(drawer.Key, open);
        try { if (!drawer.Row.IsDisposed) drawer.Row.SetExpanded(open); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: drawer marker threw: " + e.Message); }
        if (!changed) return;
        UiLayoutBatch.Begin();
        try { ApplyFilter(); }
        finally { UiLayoutBatch.End(); }
        try { DrawerToggled?.Invoke(GroupNavCaption(drawer.Section), open); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: drawer callback threw: " + e.Message); }
    }

    /// <summary>The readback of every drawer: <c>{group, key, open, count, declared}</c>.</summary>
    internal List<object?> DrawersReadback()
    {
        var list = new List<object?>(_drawers.Count);
        foreach (var d in _drawers)
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["group"] = GroupNavCaption(d.Section),
                ["key"] = d.Key,
                ["open"] = d.Open,
                ["count"] = d.Shown,
                ["declared"] = SettingsDrawers.CountOf(d.Section),
            });
        return list;
    }

    /// <summary>A section heading's <c>page-section</c> readback, read now (a drawer adds <c>drawer</c>, <c>open</c>, <c>count</c>).</summary>
    private Dictionary<string, object?> DescribeSection(ModSettingsSection section)
    {
        var d = new Dictionary<string, object?>
        {
            ["title"] = section.Title,
            ["scope"] = SettingScopes.Word(section.Scope),
            ["kind"] = ModSettingsSection.NameOf(section.Kind),
            ["rows"] = section.Rows.Count,
            ["items"] = section.Items.Count,
            ["group"] = GroupNavCaption(section),
            ["drawer"] = false,
        };
        foreach (var drawer in _drawers)
        {
            if (!ReferenceEquals(drawer.Section, section)) continue;
            d["drawer"] = true;
            d["open"] = drawer.Open;
            d["count"] = drawer.Shown;
            d["key"] = drawer.Key;
            // Live polish 2026-10-06: the fold marker left of the title; its state for the bridge.
            d["chevron"] = new Dictionary<string, object?>
            {
                ["cell"] = ModSettingsScopes.CellDrawerChevron,
                ["state"] = drawer.Open ? "open" : "closed",
                ["points"] = drawer.Open ? "down" : "right",
                ["side"] = "left",
                ["tone"] = "gold",
            };
            // Live 2026-10-06: the header as placed now, after every re-layout (a value change's count refresh, the scroll
            // restore, a rebuild, a resolution change): the marker drawn inside the row, the whole title visible.
            var fit = DrawerHeadingFitNow(drawer);
            if (fit != null)
            {
                ((Dictionary<string, object?>)d["chevron"]!)["shown"] = fit["chevronShown"];
                d["titleShown"] = fit["titleShown"];
                d["heading"] = fit;
            }
        }

        if (DrawerScope.Length > 0 && !(d["drawer"] is true)) d["count"] = SettingsDrawers.CountOf(section);
        return d;
    }

    /// <summary>
    /// The drawer header's fit from its drawn rects (<see cref="ModSettingsScopes.DrawerHeadingFit"/>), or null when the
    /// runtime reports no rects (the header was never placed).
    /// </summary>
    private static Dictionary<string, object?>? DrawerHeadingFitNow(DrawerState drawer)
    {
        try
        {
            if (drawer.Row.IsDisposed) return null;
            var described = drawer.Row.Describe();
            if (!described.TryGetValue("rect", out var r) || r is not Dictionary<string, object?> rr
                || rr["x"] is not float rowLeft || rr["w"] is not float rowWidth) return null;
            var cells = new List<(string Key, float Left, float Width)>();
            if (described.TryGetValue("cells", out var c) && c is List<object?> list)
                foreach (var cell in list)
                    if (cell is Dictionary<string, object?> cd && cd.TryGetValue("rect", out var cr) && cr is Dictionary<string, object?> rect
                        && rect["x"] is float x && rect["w"] is float w)
                        cells.Add((cd["key"] as string ?? "", x, w));
            return ModSettingsScopes.DrawerHeadingFit(rowLeft, rowWidth, cells, drawer.Section.Title);
        }
        catch { return null; }
    }

    /// <summary>The settings header: heading, then the search field and Reset all at the right.</summary>
    private void BuildHeader(IUiPanel panel, Action<IUiHandle, string, object?> add)
    {
        // MENUKIT2 round C: a small action row in the grid (its width is the rows', so Reset all never sits
        // over the description column): "Settings" at the name size, then the search and Reset all.
        // MODPAGE2: the title starts at the page margin, like every other line of the page.
        var cells = new List<CellSpec>(5)
        {
            ModPageSpacing.Lead(),
            new CellSpec(CellKind.Muted, CellTitle).With(
                text: HeaderTitle, fontSize: GameSettingsLook.ActionRowText, tone: CellTone.Muted),
            new CellSpec(CellKind.Spacer, HeadBlockLayout.CellGap),
        };
        if (SearchShown)
        {
            cells.Add(new CellSpec(CellKind.Input, CellSearch).With(
                text: _query, placeholder: SearchCaption, width: CellWidth.Px(SearchCellWidth), minWidth: SearchCellWidth,
                skin: CellSkin.ValueBox));
        }

        if (Back != null)
            cells.Add(new CellSpec(CellKind.Button, CellBack).With(text: BackText, align: CellAlign.Right, skin: CellSkin.KeyCap));
        cells.Add(new CellSpec(CellKind.Button, CellResetAll).With(text: ResetAllCaption, align: CellAlign.Right, skin: CellSkin.KeyCap));
        var row = panel.CellRow(new CellRowSpec(
            HeaderKey, cells, height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true));
        HeaderRow = row;
        if (SearchShown)
        {
            // Filtering hides and shows the rows already drawn, so it can run per keystroke: nothing
            // is rebuilt, and no control the player is about to click is destroyed.
            row.OnChanged(CellSearch, text => ApplySearch(text));
            add(row, RowSearch, (Func<object?>)(() => new Dictionary<string, object?>
            {
                ["query"] = _query,
                ["total"] = TotalRows,
                ["shown"] = ShownRows,
                ["threshold"] = ModSettingsLayout.SearchThreshold,
            }));
        }

        row.OnClick(CellResetAll, PressResetAll);
        if (Back != null)
        {
            var back = Back;
            row.OnClick(CellBack, () => back());
        }
        // MENUKIT2: the description column (what it says now, and where from) and the borrowed game art.
        add(row, ModSettingsDescription.ReadbackKey, (Func<object?>)(() =>
        {
            var d = Description().Readback();
            d["settingsArt"] = SettingsArtLadder.Describe();
            return d;
        }));
        // CONTROLS1 round 2: what the runtime actually drew (sprites, colors, rims per control), read live.
        add(row, RowDrawn, (Func<object?>)(() => (object?)_panel?.SettingsDrawn() ?? new Dictionary<string, object?> { ["runtime"] = "none" }));
        add(row, RowResetAll, (Func<object?>)(() => new Dictionary<string, object?>
        {
            ["resettable"] = ResettableRows().Count,
            ["hidden"] = HiddenByShowIf,
            ["pendingRestart"] = AnyPendingRestart(),
        }));
    }

    /// <summary><b>Reset all</b>: ask the owner (which opens the dialog) when anything differs from its default.</summary>
    internal void PressResetAll()
    {
        if (ResettableRows().Count == 0)
        {
            Plugin.Logger?.LogInfo("Ui: settings reset-all for " + _pluginId + ": nothing differs from its default.");
            return;
        }

        ResetAll?.Invoke();
    }

    /// <summary>A section heading: small, muted, against the bottom of a row a little taller than one line.</summary>
    internal static CellRowSpec SectionRow(int index, string title) => SectionRow(index, title, openKeybinds: false);

    /// <summary>
    /// A section heading; KEYS1: the keys section's carries a small <b>Open in Keybinds</b> button at
    /// its right (the title then takes the rest of the row).
    /// </summary>
    internal static CellRowSpec SectionRow(int index, string title, bool openKeybinds)
    {
        // The heading takes the whole row: an auto width is only an estimate of the caption and
        // cut short titles such as "Companion" live.
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellTitle).With(
                text: title ?? "", width: CellWidth.Fraction(openKeybinds ? 0.7f : 1f), fontSize: SectionFontSize,
                vAlign: CellVAlign.Bottom, tone: CellTone.Muted),
        };
        // CONTROLS1 round 3: this is the row the page draws for an author section (ModSettingsScopes.SubheadingRow),
        // so its Open in Keybinds takes the game button art like Reset all (live 2026-10-03 it was the only
        // header button left on the kit's flat fill: this spec had no skin). A heading is never a list row:
        // plain, so the pointer never lights it.
        if (openKeybinds)
            cells.Add(new CellSpec(CellKind.Button, CellOpenKeybinds).With(text: OpenKeybindsCaption, align: CellAlign.Right, skin: CellSkin.KeyCap));
        return new CellRowSpec(
            SectionKeyPrefix + index.ToString(CultureInfo.InvariantCulture), cells,
            height: SectionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true);
    }

    /// <summary>
    /// MENUKIT2: a section heading as the game's <c>CustomGameSettings_Label</c> (M1): the title at the
    /// game's size and 0.906 gray, bottom-aligned in a <see cref="ModSettingsRowGrid.HeaderHeight"/> row; the
    /// runtime draws the <c>Divider02_LeftToRight</c> rule under its left half (<see cref="SettingsArtPart.SectionRule"/>).
    /// No filled band. Not drawn yet: <see cref="SectionRow(int, string, bool)"/> is, until the page's drawing
    /// switches to the game-look grid.
    /// </summary>
    internal static CellRowSpec GameSectionRow(int index, string title, bool openKeybinds)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellTitle).With(
                text: title ?? "", width: CellWidth.Fraction(openKeybinds ? 0.7f : 1f),
                fontSize: ModSettingsRowGrid.HeaderFontSize, vAlign: CellVAlign.Middle),
        };
        if (openKeybinds)
            cells.Add(new CellSpec(CellKind.Button, CellOpenKeybinds).With(text: OpenKeybindsCaption, align: CellAlign.Right, skin: CellSkin.KeyCap));
        return new CellRowSpec(
            SectionKeyPrefix + index.ToString(CultureInfo.InvariantCulture), cells,
            height: ModSettingsRowGrid.HeaderHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true);
    }

    /// <summary>KEYS1: the automatic Keys section, or a builder section holding a keybind row.</summary>
    internal static bool IsKeysSection(ModSettingsSection section)
    {
        if (section.Kind == ModSettingsSectionKind.Keys) return true;
        foreach (var item in section.Items)
            if (item.Row is { Kind: SettingsEditorKind.Keybind }) return true;
        return false;
    }

    /// <summary>
    /// The search field changed. The filter is applied to the rows already drawn; a repeat of the
    /// same text does nothing.
    /// </summary>
    internal void ApplySearch(string? text)
    {
        string next = (text ?? "").Trim();
        if (string.Equals(next, _query, StringComparison.Ordinal)) return;
        _query = next;
        ApplyFilter();
        try { Search?.Invoke(_query); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings search callback threw: " + e.Message); }
    }

    /// <summary>
    /// Re-evaluate every row's show-if, enable-if and needs-server-part, apply them in place (hide,
    /// gray, rewrite the help line) and re-filter. Called on build and after every committed change.
    /// </summary>
    internal void Reevaluate(bool notify = true)
    {
        UiLayoutBatch.Begin();
        try { ApplyPredicates(); }
        finally { UiLayoutBatch.End(); }
        if (notify) NotifyPending();
    }

    private void ApplyPredicates()
    {
        // Settings scopes (step 4): every row's state is the framework's availability decision
        // (SettingAvailability) over the joined server's evidence, with the author's show-if / enable-if
        // and hide-when-alone as its inputs.
        _context = ReadContext();
        for (int i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            var options = binding.Item.Options;
            bool shownByIf = Evaluate(options.ShowIf, binding.Item, showIf: true);
            bool enabledByIf = Evaluate(options.EnableIf, binding.Item, showIf: false);
            bool shown;
            bool enabled;
            string why;
            // Three homes: a link row goes to another place in the Mods window; it is never grayed by the joined server's
            // facts, even under a server scope (Progenitor's page's link to the Server tab works when not connected).
            if (binding.Item.Kind is ModSettingsItemKind.Divider or ModSettingsItemKind.Link)
            {
                shown = shownByIf;
                enabled = true;
                why = "";
                binding.Availability = new SettingAvailabilityResult(SettingRowState.Editable, "", "");
            }
            else
            {
                var facts = ModSettingsScopes.FactsOf(binding.Item, binding.Section.Scope, _context, shownByIf, enabledByIf);
                var result = SettingAvailability.Decide(facts);
                binding.Availability = result;
                shown = result.State != SettingRowState.Hidden;
                enabled = result.State is SettingRowState.Editable or SettingRowState.AdminOnly;
                why = binding.AdminOnly && result.State != SettingRowState.Grayed
                    ? SettingAvailability.AdminOnlyReason
                    : ModSettingsScopes.WhyOf(result, options);
                // Settings organization stage 5: an admin-channel row the server keeps out of the game is read-only
                // with the server's note, and shows the value the server answered with last.
                if (binding.Item.Admin is { } admin) AdminPredicates(binding, admin, ref enabled, ref why);
                else if (binding.LockedBy != null && result.State != SettingRowState.Grayed) why = ServerSettingsLock.NoteFor(binding.LockedBy);
            }

            // PENDING1: a status row repaints its line and tone whenever the page is re-evaluated (a rules answer).
            if (binding.Item.Options.StatusTone != null)
            {
                try { binding.Sync(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings status row threw: " + e.Message); }
            }

            bool framework = binding.Availability.State == SettingRowState.Grayed
                && binding.Availability.Code != SettingAvailability.AuthorCode;
            bool changed = binding.ShownByIf != shown || binding.Enabled != enabled
                || !string.Equals(binding.Why, why, StringComparison.Ordinal);
            binding.ShownByIf = shown;
            binding.Enabled = enabled;
            binding.ServerPartGreyed = framework;
            binding.ServerPartReason = framework ? binding.Availability.Reason : "";
            binding.Why = why;
            for (int j = 0; j < binding.Handles.Count; j++)
            {
                var handle = binding.Handles[j];
                if (handle == null || handle.IsDisposed) continue;
                try { if (handle.Interactable != enabled) handle.Interactable = enabled; }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings row gray threw: " + e.Message); }
            }

            if (changed || !binding.HelpWritten) WriteHelp(binding);
        }

        ApplyFilter();
    }

    private ServerPartState CurrentServerPart()
    {
        if (_serverPart == null) return ServerPartState.Unknown;
        try { return _serverPart(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings server-part read threw: " + e.Message);
            return ServerPartState.Unknown;
        }
    }

    /// <summary>A show-if / enable-if: null is true; a throw is logged once per row and read as true.</summary>
    private static bool Evaluate(Func<bool>? predicate, ModSettingsItem item, bool showIf)
    {
        if (predicate == null) return true;
        try { return predicate(); }
        catch (Exception e)
        {
            bool faulted = showIf ? item.Options.ShowIfFaulted : item.Options.EnableIfFaulted;
            if (!faulted)
                Plugin.Logger?.LogWarning(
                    "Ui: settings " + (showIf ? "show-if" : "enable-if") + " of " + item.Id + " threw: " + e.Message
                    + " (logged once; the row is treated as " + (showIf ? "shown" : "enabled") + ").");
            if (showIf) item.Options.ShowIfFaulted = true;
            else item.Options.EnableIfFaulted = true;
            return true;
        }
    }

    private void NotifyPending()
    {
        bool now = AnyPendingRestart();
        if (now == _lastPending) return;
        _lastPending = now;
        try { PendingRestartChanged?.Invoke(now); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings pending-restart callback threw: " + e.Message); }
    }

    /// <summary>Show exactly the headings and items the current query and the show-if predicates allow.</summary>
    private void ApplyFilter()
    {
        string q = Query;
        var visibleSections = new HashSet<ModSettingsSection>();
        for (int i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            bool match = q.Length == 0
                || (AuthorTitle(binding.Section).Length > 0 && AuthorTitle(binding.Section).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                || ModSettingsLayout.Matches(binding.Item, q);
            if (binding.Item.Kind == ModSettingsItemKind.Divider)
                match = q.Length == 0 || (AuthorTitle(binding.Section).Length > 0 && AuthorTitle(binding.Section).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            binding.Visible = match && binding.ShownByIf;
            if (binding.Visible && binding.Item.Kind != ModSettingsItemKind.Divider) visibleSections.Add(binding.Section);
        }

        // Three homes: a drawer's count is the settings a player can see in it; a closed drawer hides its rows (a search
        // looks inside every drawer, so it hides nothing while a query is typed).
        foreach (var drawer in _drawers) drawer.Shown = 0;
        for (int i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            if (!binding.Visible || !SettingsDrawers.Counts(binding.Item)) continue;
            foreach (var drawer in _drawers)
                if (ReferenceEquals(drawer.Section, binding.Section)) drawer.Shown++;
        }

        foreach (var drawer in _drawers)
        {
            if (drawer.Row.IsDisposed) continue;
            try { drawer.Row.SetCell(ModSettingsScopes.CellDrawerCount, ModSettingsScopes.DrawerCountText(drawer.Shown)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: drawer count threw: " + e.Message); }
        }

        for (int i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            bool visible = binding.Visible
                && (binding.Item.Kind != ModSettingsItemKind.Divider || visibleSections.Contains(binding.Section));
            binding.Visible = visible;
            if (visible && q.Length == 0 && ClosedDrawerOf(binding.Section)) visible = false;
            for (int j = 0; j < binding.Handles.Count; j++) SetVisible(binding.Handles[j], visible);
            if (binding.Reset != null)
            {
                SetVisible(binding.Reset, visible && ResetShown(binding));
                PaintReset(binding);
            }
        }

        for (int i = 0; i < _headings.Count; i++)
            SetVisible(_headings[i].Heading, visibleSections.Contains(_headings[i].Section));

        // Settings scopes: a scope section shows while any of its rows does; an empty one is omitted.
        for (int i = 0; i < _scopeHeadings.Count; i++)
        {
            var (group, heading, line) = _scopeHeadings[i];
            bool any = false;
            foreach (var section in group.Sections) any |= visibleSections.Contains(section);
            SetVisible(heading, any);
            SetVisible(line, any);
            if (line is IUiCellRow lineRow && !lineRow.IsDisposed)
            {
                try { lineRow.SetCell(ModSettingsScopes.CellScopeLine, ScopeLineText(), visible: true); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings scope line rewrite threw: " + e.Message); }
            }
        }

        if (_noMatches != null) SetVisible(_noMatches, visibleSections.Count == 0 && q.Length > 0);
    }

    private bool ClosedDrawerOf(ModSettingsSection section)
    {
        foreach (var drawer in _drawers)
            if (ReferenceEquals(drawer.Section, section)) return !drawer.Open;
        return false;
    }

    /// <summary>The arrow shows with its row (dim at the default; <see cref="ResetState"/>).</summary>
    private static bool ResetShown(ItemBinding binding) => binding.Item.Row != null;

    private static void SetVisible(IUiHandle? handle, bool visible)
    {
        if (handle == null || handle.IsDisposed) return;
        try { if (handle.Visible != visible) handle.Visible = visible; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings row visibility threw: " + e.Message); }
    }

    /// <summary>
    /// The rows <b>Reset all</b> acts on and counts: <see cref="ModSettingsLayout.InResetAll"/>, less
    /// any row show-if hides or enable-if / needs-server-part grays — the same rows whose own reset
    /// link would work. Before the page is drawn, every <see cref="ModSettingsLayout.Resettable"/> row.
    /// </summary>
    internal List<SettingsRow> ResettableRows()
    {
        // Settings scopes: Reset all covers Your settings only; Server settings keep their own reset arrows.
        if (_bindings.Count == 0)
        {
            var personal = new List<ModSettingsSection>();
            foreach (var section in _sections) if (section.Scope == SettingScope.Personal) personal.Add(section);
            return ModSettingsLayout.Resettable(personal);
        }

        var rows = new List<SettingsRow>();
        for (int i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            if (binding.Section.Scope != SettingScope.Personal) continue;
            if (binding.Item.Row is not { } row || !binding.Enabled || !binding.ShownByIf) continue;
            if (ModSettingsLayout.InResetAll(row) && !rows.Exists(r => r.RowId == row.RowId)) rows.Add(row);
        }

        return rows;
    }

    /// <summary>Close pending server requests and open confirmations, and forget callbacks. The page's panel disposes the widgets.</summary>
    internal void Release()
    {
        // TEXTEXPAND1: the editor belongs to the page: it closes (discarding) when the page goes.
        var expand = _expandDialog;
        _expandDialog = null;
        try { expand?.Close(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings editor close threw: " + e.Message); }
        if (ReferenceEquals(Drawn, this)) Drawn = null;
        CloseAdminConfirm();
        for (int i = 0; i < _requests.Count; i++)
        {
            try { _requests[i].Dispose(); }
            catch { /* a request handle is released once */ }
        }

        _requests.Clear();
        for (int i = 0; i < _bindings.Count; i++)
        {
            for (int j = 0; j < _bindings[i].Handles.Count; j++)
            {
                if (_bindings[i].Handles[j] is UiButtonHandle { ConfirmDialog: { } dialog })
                {
                    try { dialog.Close(); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings confirmation close threw: " + e.Message); }
                }
            }
        }

        // Drop the page's copies of the plugin's button actions and info functions (the predicates
        // belong to the registration and go with it).
        for (int i = 0; i < _sections.Count; i++)
            for (int j = 0; j < _sections[i].Items.Count; j++) _sections[i].Items[j].Release();
        _bindings.Clear();
        _headings.Clear();
        _drawers.Clear();
        DrawerToggled = null;
        _noMatches = null;
        _hoveredId = null;
        _selectedId = null;
        _optionItemId = null;
        _option = default;
        DescriptionChanged = null;
        _panel = null;
        Search = null;
        ResetAll = null;
        Back = null;
        PendingRestartChanged = null;
    }

    /// <summary>What one setting row's readback says, read now (stage 1 keys plus the stage 2 facts).</summary>
    internal static Dictionary<string, object?> Describe(SettingsRow row, ModSettingsSection? section, SettingControl control) =>
        Describe(row, section, control, null, null);

    private static Dictionary<string, object?> Describe(
        SettingsRow row, ModSettingsSection? section, SettingControl control, ModSettingOptions? options, ItemBinding? binding)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.RowId,
            ["kind"] = "setting",
            ["section"] = AuthorTitle(section),
            ["key"] = row.Key,
            ["label"] = row.Label,
            ["displayName"] = ModSettingsLayout.DisplayNameOf(row, options),
            ["control"] = ModSettingsLayout.NameOf(control),
            ["value"] = SafeRead(row),
            ["default"] = SafeDefault(row),
            ["modified"] = ModSettingsLayout.IsModified(row),
            ["resettable"] = ModSettingsLayout.CanReset(row),
            ["editPath"] = row.EditPath.ToString(),
            ["scope"] = SettingScopes.LegacyName(row.Scope),
            ["scopeDeclared"] = options?.Scope is { } scope ? (scope == SettingScope.Server ? "server" : "local") : "",
            ["bounds"] = SafeBounds(row),
            ["help"] = !string.IsNullOrWhiteSpace(options?.Help) ? options!.Help : row.Description,
            ["helpLine"] = binding?.HelpLine ?? ModSettingsLayout.HelpText(row, options, control, null, null),
            ["visible"] = binding?.Visible ?? true,
            ["enabled"] = binding?.Enabled ?? true,
            ["why"] = binding?.Why ?? "",
            ["needsRestart"] = options?.NeedsRestart ?? false,
            ["pending"] = ModSettingsLayout.IsPendingRestart(row, options),
            ["needsServerPart"] = options?.NeedsServerPart ?? false,
            ["greyed"] = binding?.ServerPartGreyed ?? false,
            ["reason"] = binding is { ServerPartGreyed: true } ? binding.ServerPartReason : "",
            ["fallback"] = options?.Fallback ?? "",
        };
        AddScopeReadback(d, section, binding);
        // Settings organization stage 2: where this stored value is at home and where else it is drawn.
        SettingPlaces.AddReadback(d, row.RowId, row.PluginId, row.Kind == SettingsEditorKind.Keybind);
        // The joined server's lock list: this row is read-only for admins too (the server refuses an edit anyway).
        d["locked"] = binding?.LockedBy != null;
        d["lockedBy"] = binding?.LockedBy;
        // Stage 3: a key in conflict names the other mod (the help line says "Also used by <mod>.").
        if (row.Kind == SettingsEditorKind.Keybind)
        {
            d["alsoUsedBy"] = ModSettingsLayout.KeyConflictOf(row);
            // KEYS1: the winner rule, as the Keybinds tab reads it.
            var verdict = ModProblems.KeyVerdict(row.PluginId, row.Key);
            d["active"] = verdict?.Active ?? true;
            d["conflict"] = verdict == null ? null : KeybindArbiter.ConflictReadback(verdict);
        }
        if (control is SettingControl.Dropdown or SettingControl.Segmented or SettingControl.Radio)
        {
            var choices = ModSettingsLayout.ChoicesFor(row);
            var values = new List<string>(choices.Count);
            for (int i = 0; i < choices.Count; i++) values.Add(choices[i].Value);
            d["choices"] = values;
        }

        if (control == SettingControl.MultiSelect)
        {
            var choices = ModSettingsLayout.MultiChoicesFor(row);
            var values = new List<string>(choices.Count);
            for (int i = 0; i < choices.Count; i++) values.Add(choices[i].Value);
            d["choices"] = values;
            d["selected"] = ModSettingsLayout.SelectedFor(row, SafeRead(row));
        }

        if (control == SettingControl.TextArea || binding is { Multiline: true })
        {
            d["multiline"] = true;
            d["lines"] = options?.Lines ?? 3;
        }

        // TEXTLIMIT1: the limits a text row is held to here — the field's cap, and for a server-wide
        // value what the server accepts (TEXTEXPAND1: whether it has an expand button).
        if (row.ValueType == typeof(string) && control is SettingControl.Text or SettingControl.TextArea or SettingControl.ServerRequest)
        {
            d["maxLength"] = control == SettingControl.ServerRequest
                ? ServerSettings.EffectiveTextLimit(row.RowId)
                : TextLimit(row, control == SettingControl.TextArea, null);
        }
        if (binding?.Expand != null) d["expandable"] = true;
        // JOINFLOW1: a date-time row (drawn as the picker, or read-only) and an admin's choice or number row.
        if (ModSettingsLayout.IsDateTimeRow(row, options)) AddDateTimeReadback(d, row, section, control, options, binding);
        if (binding is { Editor.Length: > 0 }) AddEditorReadback(d, row, binding);
        // SAVEHOLD1: the last page write of this row, and whether the cfg on disk now holds it.
        try
        {
            var persist = SettingPersistence.Describe(row.RowId, row.EntryForReadback);
            if (persist != null) d["persist"] = persist;
        }
        catch { /* no readback */ }

        if (control == SettingControl.Number || control == SettingControl.Slider)
        {
            var spec = ModSettingsLayout.NumberFor(row);
            d["min"] = spec.Min;
            d["max"] = spec.Max;
            d["step"] = spec.Step;
            d["units"] = spec.Units;
        }

        return d;
    }

    /// <summary>
    /// Settings scopes: <c>scopeSection</c> (<c>personal</c> / <c>server-wide</c>: the section the row is
    /// drawn under), <c>owner</c>, <c>adminOnly</c>, <c>hideWhenAlone</c> and <c>availability</c>
    /// <c>{state, code, reason}</c> (<see cref="SettingAvailability"/>). <c>scope</c> keeps its old meaning.
    /// </summary>
    /// <summary>The author's section title (kept in the readback when the drawn subheading was dropped as redundant).</summary>
    private static string AuthorTitle(ModSettingsSection? section) =>
        section == null ? "" : section.SourceTitle.Length > 0 ? section.SourceTitle : section.Title;

    private static void AddScopeReadback(Dictionary<string, object?> d, ModSettingsSection? section, ItemBinding? binding)
    {
        var scope = section?.Scope ?? SettingScope.Personal;
        d["scopeSection"] = SettingScopes.Word(scope);
        d["owner"] = SettingScopes.Word(scope == SettingScope.ServerWide ? SettingOwner.Server : SettingOwner.Client);
        d["adminOnly"] = binding?.AdminOnly ?? false;
        d["hideWhenAlone"] = binding?.Item.Options.WhenAlone == AloneBehavior.Hide;
        // Live 2026-10-06: the caption the runtime draws in the name column (null: the runtime cannot tell). A row whose
        // field is drawn with an empty caption is the inline-Apply regression; live checks assert captionShown.
        if (binding?.Main is { IsDisposed: false } main && main.RowCaption() is { } caption)
        {
            d["caption"] = caption;
            d["captionShown"] = caption.Trim().Length > 0;
        }
        else if (binding?.DateField is UiDateTimeField { IsDisposed: false } date)
        {
            // A date field is a cell row whose name cell is its caption (WIPECONFIRM1's Next wipe, JOINFLOW1's Next reset).
            d["caption"] = date.Caption;
            d["captionShown"] = date.Caption.Trim().Length > 0;
        }

        // FIELDAPPLY1: a date row's Set/Change button, for mods_ui click_at {row: "<dateRow>/<openCell>"}.
        if (binding?.DateField is UiDateTimeField { IsDisposed: false } dated)
        {
            d["dateRow"] = dated.Row.Key;
            d["openCell"] = UiDateTimeLayout.CellOpen;
        }

        AddInlineButtonReadback(d, binding);
        // NOPROGMSG1: the muted hint an empty field shows (the default an empty value stands for).
        if (binding?.Main is UiHandle { Placeholder.Length: > 0 } hinted) d["placeholder"] = hinted.Placeholder;

        var a = binding?.Availability ?? new SettingAvailabilityResult(SettingRowState.Editable, "", "");
        d["availability"] = new Dictionary<string, object?>
        {
            ["state"] = a.State switch
            {
                SettingRowState.AdminOnly => "admin-only",
                SettingRowState.Grayed => "grayed",
                SettingRowState.Hidden => "hidden",
                _ => "editable",
            },
            ["code"] = a.Code,
            ["reason"] = a.Reason,
        };
    }

    /// <summary>
    /// INLINEAPPLY1 (user review 2026-10-06: the inline Apply was a flat gray block beside the steel buttons): a row's action
    /// button (an inline Apply, the Apply under a text area) drawn as the game's button through the shared game-button
    /// path, and listed for the readback's <c>inlineButtons</c>.
    /// </summary>
    private static void UseGameButton(ItemBinding binding, IUiButton button)
    {
        if (button == null) return;
        button.GameButton();
        binding?.InlineButtons.Add(button);
    }

    /// <summary>INLINEAPPLY1: <c>inlineButtons [{caption, art}]</c>, <c>art</c> <c>game-button</c> when the steel art is drawn.</summary>
    private static void AddInlineButtonReadback(Dictionary<string, object?> d, ItemBinding? binding)
    {
        if (binding == null || binding.InlineButtons.Count == 0) return;
        var list = new List<object?>();
        foreach (var b in binding.InlineButtons)
        {
            if (b == null || b.IsDisposed) continue;
            string caption;
            try { caption = b.Text; }
            catch { caption = ""; }
            list.Add(new Dictionary<string, object?> { ["caption"] = caption, ["art"] = b.ButtonArt() });
        }

        d["inlineButtons"] = list;
    }

    /// <summary>NOPROGMSG1: repaint a status row every <paramref name="seconds"/> until it is disposed (the info lines' scheduler).</summary>
    private static void ScheduleStatus(IUiCellRow row, Action paint, double seconds)
    {
        if (seconds <= 0) return;
        var scheduler = UiInfoHandle.Scheduler;
        if (scheduler == null) return;
        object? owner = (row as UiHandle)?.Owner;
        double every = Math.Min(UiInfoHandle.MaxRefreshSeconds, Math.Max(UiInfoHandle.MinRefreshSeconds, seconds));
        void Tick()
        {
            if (row.IsDisposed) return;
            try { if (row.Visible) paint(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: status row tick threw: " + e.Message); }
            try { scheduler(every, Tick, owner); }
            catch { /* the row keeps its last line */ }
        }

        try { scheduler(every, Tick, owner); }
        catch { /* painted once */ }
    }

    /// <summary>NOPROGMSG1: a server text row's field ended an edit (the draft changed); status rows that preview it repaint.</summary>
    internal static event Action<string>? DraftChanged;

    /// <summary>PENDING1: a status row's cell-row key (<c>status/Pending changes</c>).</summary>
    private static string StatusRowKey(ModSettingsItem item) => "status/" + item.Label;

    private static string SafeInfo(Func<string> source)
    {
        try { return source() ?? ""; }
        catch { return ""; }
    }

    /// <summary>The readback key of a button or info line: its id after the plugin id (<c>button/Run</c>, <c>info/Local time#2</c>).</summary>
    internal static string ExtraKey(ModSettingsItem item)
    {
        string id = item.Id ?? "";
        foreach (string marker in new[] { "/button/", "/info/" })
        {
            int at = id.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0) return id.Substring(at + 1);
        }
        return (item.Kind == ModSettingsItemKind.Button ? "button/" : "info/") + item.Label;
    }

    private Dictionary<string, object?> DescribeExtra(ItemBinding binding)
    {
        var item = binding.Item;
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = item.Id,
            // COUNTERPART-SAMPLES (live): a button or info line has no cfg key; readers printed
            // "undefined". Its key is the id's tail ("button/Run", "info/Local time") and its kind says
            // it is not a setting.
            ["kind"] = item.Kind switch { ModSettingsItemKind.Button => "button", ModSettingsItemKind.Link => "link", _ => "info" },
            ["key"] = item.Kind == ModSettingsItemKind.Link ? item.LinkKey : ExtraKey(item),
            ["section"] = AuthorTitle(binding.Section),
            ["control"] = item.Kind switch { ModSettingsItemKind.Button => "button", ModSettingsItemKind.Link => "link", _ => "info" },
            ["label"] = item.Label,
            ["displayName"] = !string.IsNullOrWhiteSpace(item.Options.Label) ? item.Options.Label : item.Label,
            ["help"] = item.Options.Help ?? "",
            ["helpLine"] = binding.HelpLine,
            ["visible"] = binding.Visible,
            ["enabled"] = binding.Enabled,
            ["why"] = binding.Why,
            ["needsServerPart"] = item.Options.NeedsServerPart,
            ["greyed"] = binding.ServerPartGreyed,
            ["reason"] = binding.ServerPartGreyed ? binding.ServerPartReason : "",
            ["fallback"] = item.Options.Fallback,
        };
        AddScopeReadback(d, binding.Section, binding);
        if (item.Kind == ModSettingsItemKind.Button) d["confirm"] = item.Confirm ?? "";
        if (item.Kind == ModSettingsItemKind.Info)
        {
            d["value"] = binding.Handles.Count > 0 && binding.Handles[0] is IUiInfo info ? info.Value : binding.StatusText ?? "";
            d["refreshSeconds"] = item.RefreshSeconds;
            if (item.Options.StatusTone != null)
            {
                // PENDING1: a status row, not a setting: its look and tone.
                d["style"] = "status";
                d["tone"] = UiCellRowHandle.ToneName(binding.StatusToneNow);
            }
        }

        return d;
    }

    private void AddItem(IUiPanel panel, ModSettingsSection section, ModSettingsItem item, Action<IUiHandle, string, object?> add)
    {
        switch (item.Kind)
        {
            case ModSettingsItemKind.Setting when item.Row != null:
                AddRow(panel, section, item, item.Row, add);
                return;
            case ModSettingsItemKind.Button:
            {
                var binding = new ItemBinding(item, section, SettingControl.Action);
                // A name on the left (the builder's label, else the caption) and a normal-size
                // button in the control column.
                string name = !string.IsNullOrWhiteSpace(item.Options.Label) ? item.Options.Label! : item.Label;
                IUiButton button = panel.NamedButton(name, item.Label);
                // Settings organization: Progenitor's uninstall row keeps the danger tone it had on the Configuration tab.
                if (item.Options.Destructive) button.Destructive();
                if (item.Confirm != null) UiSettingsControls.Confirm(panel, button, item.Label, item.Confirm);
                button.OnClick(() => RunButton(binding));
                binding.Handles.Add(button);
                binding.Main = button;
                _bindings.Add(binding);
                WatchHover(panel, binding);
                add(button, RowSetting, (Func<object?>)(() => DescribeExtra(binding)));
                AddExtraReadback(button, item, add);
                return;
            }
            case ModSettingsItemKind.Info when item.InfoValue != null && item.Options.StatusTone != null && panel is UiPanelHandle:
            {
                // PENDING1: a status row (the Local server group's muted info row): its name, then the line, muted or in gold.
                var binding = new ItemBinding(item, section, SettingControl.ReadOnly);
                var source = item.InfoValue;
                var toneOf = item.Options.StatusTone;
                string key = StatusRowKey(item);
                string label = !string.IsNullOrWhiteSpace(item.Options.Label) ? item.Options.Label! : item.Label;
                // The page's rows are already on the settings grid (Build turns it on for the whole page).
                // TOKENTIPS2: a wrapping status row keeps every line (the Placeholders tips), sized to its text.
                IUiCellRow row = panel.CellRow(item.Options.StatusWrap
                    ? ConfigurationPane.WrappingStatusRowSpec(key, label, SafeInfo(source), StatusWrapEstimateWidth, item.Options.StatusWrapLines)
                    : ConfigurationPane.StatusRowSpec(key, label, SafeInfo(source)));
                binding.StatusText = SafeInfo(source);
                void Paint()
                {
                    if (row.IsDisposed) return;
                    string text = SafeInfo(source);
                    CellTone tone;
                    try { tone = toneOf(); }
                    catch { tone = CellTone.Muted; }
                    binding.StatusText = text;
                    binding.StatusToneNow = tone;
                    row.SetCell(ConfigurationPane.CellStatusValue, text, tone, true);
                }

                Paint();
                binding.Sync = Paint;
                // NOPROGMSG1: a preview repaints as soon as a field's edit ends.
                Action<string> onDraft = _ => Paint();
                DraftChanged += onDraft;
                _requests.Add(new ActionLease(() => DraftChanged -= onDraft));
                // NOPROGMSG1: a status row that refreshes is repainted on the info lines' clock while the page is up.
                ScheduleStatus(row, Paint, item.RefreshSeconds);
                binding.Handles.Add(row);
                binding.Main = row;
                _bindings.Add(binding);
                WatchHover(panel, binding);
                add(row, RowSetting, (Func<object?>)(() => DescribeExtra(binding)));
                AddExtraReadback(row, item, add);
                return;
            }
            case ModSettingsItemKind.Info when item.InfoValue != null:
            {
                var binding = new ItemBinding(item, section, SettingControl.ReadOnly);
                var source = item.InfoValue;
                // Decided 2026-10-06: an empty read-only value reads "Not set" (muted by the runtime).
                var info = panel.Info(item.Label, () => OrNotSet(source()), item.RefreshSeconds);
                binding.Handles.Add(info);
                binding.Main = info;
                _bindings.Add(binding);
                WatchHover(panel, binding);
                add(info, RowSetting, (Func<object?>)(() => DescribeExtra(binding)));
                AddExtraReadback(info, item, add);
                return;
            }
            case ModSettingsItemKind.Link when item.ButtonAction != null:
            {
                // Settings organization: a link row — the sentence and Open — keyed for the bridge (<key>/open).
                var binding = new ItemBinding(item, section, SettingControl.Action);
                var row = panel.CellRow(SettingsLinks.Row(item.LinkKey, item.Label, SettingsLinks.CaptionFor(item.LinkTarget)));
                row.OnClick(SettingsLinks.CellOpen, () => RunButton(binding));
                binding.Handles.Add(row);
                binding.Main = row;
                _bindings.Add(binding);
                WatchHover(panel, binding);
                string target = item.LinkTarget;
                string label = item.Label;
                add(row, ConfigurationPane.RowPageLink, (Func<object?>)(() => SettingsLinks.Readback(target, SettingsLinks.LandingTop, label)));
                add(row, RowSetting, (Func<object?>)(() => DescribeExtra(binding)));
                return;
            }
            case ModSettingsItemKind.Admin when item.Admin != null:
                AddAdminRow(panel, section, item, item.Admin, add);
                return;
            case ModSettingsItemKind.Divider:
            {
                var binding = new ItemBinding(item, section, SettingControl.ReadOnly);
                var divider = panel.Divider();
                binding.Handles.Add(divider);
                binding.HelpWritten = true;
                _bindings.Add(binding);
                add(divider, RowDivider, new Dictionary<string, object?> { ["section"] = AuthorTitle(section) });
                return;
            }
        }
    }

    /// <summary>Settings organization: the item's one more readback key (<see cref="ModSettingOptions.ExtraReadback"/>), if any.</summary>
    private static void AddExtraReadback(IUiHandle handle, ModSettingsItem item, Action<IUiHandle, string, object?> add)
    {
        var options = item.Options;
        if (options.ExtraReadback == null || string.IsNullOrEmpty(options.ExtraReadbackKey)) return;
        add(handle, options.ExtraReadbackKey!, (Func<object?>)(() =>
        {
            var read = options.ExtraReadback;
            return read?.Invoke();
        }));
    }

    private void RunButton(ItemBinding binding)
    {
        if (!binding.Enabled)
        {
            Plugin.Logger?.LogInfo("Ui: settings button " + binding.Item.Id + " refused: " + binding.Why);
            return;
        }

        var action = binding.Item.ButtonAction;
        if (action == null) return;
        string status;
        try
        {
            action();
            status = "Done.";
            Plugin.Logger?.LogInfo("Ui: settings button " + binding.Item.Id + " ran.");
        }
        catch (Exception e)
        {
            status = "Failed: " + e.Message;
            Plugin.Logger?.LogWarning("Ui: settings button " + binding.Item.Id + " threw: " + e.Message);
        }

        binding.Status = status;
        Select(binding.Item.Id);
        WriteHelp(binding);
        Reevaluate();
    }

    private void AddRow(
        IUiPanel panel, ModSettingsSection section, ModSettingsItem item, SettingsRow row, Action<IUiHandle, string, object?> add)
    {
        var options = item.Options;
        var control = ModSettingsLayout.DrawnControl(row, options);
        // Settings scopes: a Server-wide row an admin could edit is read-only for everyone else —
        // `Admin only · <value>` — instead of an Apply the server would refuse.
        bool adminOnly = control == SettingControl.ServerRequest && !_context.CanEditServerWide;
        // The operator's lock list ([ServerSettings] LockedInGame on the joined server): read-only for admins too.
        string? lockedBy = control == SettingControl.ServerRequest && !adminOnly ? ServerSettingsLock.ClientLockedBy(row.RowId) : null;
        if (adminOnly || lockedBy != null) control = SettingControl.ReadOnly;
        string caption = Caption(row, options, control);
        var binding = new ItemBinding(item, section, control) { AdminOnly = adminOnly, LockedBy = lockedBy };
        IUiHandle handle;

        switch (control)
        {
            case SettingControl.Toggle:
            {
                var toggle = panel.Toggle(caption, IsTrue(SafeRead(row)));
                toggle.OnChanged(v =>
                {
                    Commit(row, v ? "true" : "false");
                    toggle.Value = IsTrue(SafeRead(row));
                    toggle.Text = caption;
                });
                binding.Sync = () => { toggle.Value = IsTrue(SafeRead(row)); toggle.Text = caption; };
                handle = toggle;
                break;
            }

            case SettingControl.Dropdown:
            case SettingControl.Segmented:
            {
                Choices(row, out var values, out var labels, out var descriptions);
                var dropdown = panel.Dropdown(caption, values, SafeRead(row), labels, descriptions);
                dropdown.OnChanged(v =>
                {
                    Commit(row, v);
                    dropdown.Value = SafeRead(row);
                });
                binding.Sync = () => dropdown.Value = SafeRead(row);
                handle = dropdown;
                break;
            }

            case SettingControl.Radio:
            {
                Choices(row, out var values, out var labels, out var descriptions);
                var options2 = UiSettingsControls.Options(values, labels, descriptions);
                var layout = UiOptionsRules.LayoutFor(options2, multi: false);
                var radio = UiSettingsControls.OptionsControl(panel, caption, options2, new[] { SafeRead(row) }, multi: false, layout);
                ((IUiRadio)radio).OnChanged(v =>
                {
                    Commit(row, v);
                    ((IUiRadio)radio).Value = SafeRead(row);
                });
                binding.Sync = () => ((IUiRadio)radio).Value = SafeRead(row);
                handle = radio;
                break;
            }

            case SettingControl.MultiSelect:
            {
                var choices = ModSettingsLayout.MultiChoicesFor(row);
                var values = new List<string>(choices.Count);
                var labels = new List<string>(choices.Count);
                for (int i = 0; i < choices.Count; i++)
                {
                    values.Add(choices[i].Value);
                    labels.Add(choices[i].Label);
                }

                var multi = panel.MultiSelect(caption, values, ModSettingsLayout.SelectedFor(row, SafeRead(row)), labels);
                multi.OnChanged((IReadOnlyList<string> selected) =>
                {
                    if (ModSettingsLayout.TryFormatSelection(row, selected, out var text, out var error, SafeRead(row))) Commit(row, text);
                    else
                    {
                        Plugin.Logger?.LogWarning("Ui: settings " + row.PluginId + " " + row.Label + " rejected: " + error);
                        Refresh(row, error);
                    }

                    multi.Selected = ModSettingsLayout.SelectedFor(row, SafeRead(row));
                });
                binding.Sync = () => multi.Selected = ModSettingsLayout.SelectedFor(row, SafeRead(row));
                handle = multi;
                break;
            }

            case SettingControl.TextArea:
            {
                var area = panel.TextArea(caption, SafeRead(row), options.Lines);
                LimitText(area, row, multiline: true);
                area.OnChanged(v =>
                {
                    Commit(row, v, trim: true);
                    LimitText(area, row, multiline: true);
                    area.Value = SafeRead(row);
                });
                binding.Sync = () =>
                {
                    LimitText(area, row, multiline: true);
                    area.Value = SafeRead(row);
                };
                binding.Input = area;
                // TEXTEXPAND1: the expand button opens the whole value in the large editor; Done commits
                // through the same path as the field's end of edit.
                AttachExpand(panel, binding, area, row, options, add,
                    limit: () => TextLimit(row, multiline: true, area.Value),
                    readOnly: false,
                    validate: null,
                    done: text =>
                    {
                        if (area.IsDisposed) return;
                        Commit(row, text, trim: true);
                        LimitText(area, row, multiline: true);
                        area.Value = SafeRead(row);
                    });
                handle = area;
                break;
            }

            case SettingControl.Number:
            {
                var spec = ModSettingsLayout.NumberFor(row);
                var number = panel.Number(
                    caption, ParseNumber(SafeRead(row)), spec.Min, spec.Max, spec.Step, spec.Decimals, spec.Units);
                number.OnChanged(v =>
                {
                    Commit(row, spec.Format(v));
                    number.Value = ParseNumber(SafeRead(row));
                });
                binding.Sync = () => number.Value = ParseNumber(SafeRead(row));
                handle = number;
                break;
            }

            case SettingControl.Slider:
            {
                var spec = ModSettingsLayout.NumberFor(row);
                var slider = panel.SliderField(
                    caption, (float)spec.Min, (float)spec.Max, (float)spec.Step, (float)ParseNumber(SafeRead(row)));
                slider.OnChanged(v =>
                {
                    Commit(row, spec.Format(v));
                    slider.Value = (float)ParseNumber(SafeRead(row));
                });
                binding.Sync = () => slider.Value = (float)ParseNumber(SafeRead(row));
                handle = slider;
                break;
            }

            case SettingControl.Color:
            {
                var color = panel.ColorField(caption, SafeRead(row), alpha: row.ToDescriptor().HasAlpha);
                color.OnChanged((string hex) =>
                {
                    Commit(row, hex);
                    color.Color = SafeRead(row);
                });
                binding.Sync = () => color.Color = SafeRead(row);
                handle = color;
                break;
            }

            case SettingControl.Keybind:
            {
                var key = panel.KeyField(caption, row.PluginId, row.Key);
                string last = SafeRead(row);
                key.OnChanged(_ =>
                {
                    if (!binding.Enabled)
                    {
                        // A grayed keybind takes no new chord; one that still arrives (the bridge) is
                        // put back and refused, like any other grayed row.
                        // Put back through the same guard as any remap (KEYS2 review): if the old key is now
                        // taken or reserved, the put-back is refused and logged rather than creating a conflict.
                        if (KeyChord.TryParse(last, out var previous)
                            && !Keybinds.TryRebind(row.PluginId, row.Key, previous, Keybinds.RebindCheck.Full, out var undoRefused))
                            Plugin.Logger?.LogWarning("Ui: settings " + row.PluginId + " " + row.Label + " could not put back " + previous + ": " + undoRefused);
                        Plugin.Logger?.LogInfo("Ui: settings " + row.PluginId + " " + row.Label + " refused: " + binding.Why);
                        Refresh(row, binding.Why);
                        return;
                    }

                    last = SafeRead(row);
                    AfterChange(row, "");
                });
                // KEYS2: a refused chord (reserved, or used by something else) shows its reason on the row;
                // the capture keeps waiting for another key.
                if (key is UiKeybindPickerHandle picker)
                    picker.Refused += reason => Refresh(row, reason);
                handle = key;
                break;
            }

            case SettingControl.Action:
            {
                var button = panel.NamedButton(ModSettingsLayout.DisplayNameOf(row), RunCaption);
                button.OnClick(() =>
                {
                    if (!binding.Enabled)
                    {
                        Plugin.Logger?.LogInfo("Ui: settings action " + row.RowId + " refused: " + binding.Why);
                        return;
                    }

                    var result = SettingsCatalog.InvokeAction(row.RowId);
                    binding.Status = result.Completed ? (result.Message.Length > 0 ? result.Message : "Done.") : result.Message;
                    WriteHelp(binding);
                    Reevaluate();
                });
                handle = button;
                break;
            }

            case SettingControl.DateTime:
                handle = AddDateTime(panel, binding, section, row, options, caption);
                break;

            case SettingControl.ServerRequest:
            {
                // JOINFLOW1: an administrator's date or choice row is drawn with its own control
                // and sends a request on each Done or pick (helpers below); a number keeps the field and Apply.
                if (AddServerControl(panel, binding, section, row, options, caption) is { } serverControl)
                {
                    handle = serverControl;
                    break;
                }

                string displayed = SafeRead(row);
                // TEXTLIMIT1: a server-wide text the builder declared multi-line is edited in the real
                // text area (Enter starts a line; line breaks travel to the server and back unchanged).
                bool multiline = ModSettingsLayout.IsTextAreaRow(row, options);
                // Settings organization review (live): a one-line field's Apply sits at the right of the field, on its
                // row; only a text area keeps Apply on a row of its own.
                if (!multiline) panel.BeginInline();
                IUiInput input;
                IUiButton apply;
                try
                {
                    input = multiline ? panel.TextArea(caption, displayed, options.Lines) : panel.Input(caption, displayed);
                    // TOKENINSERT1: field, Insert, Players see, then Apply (a field TextTokens lists).
                    if (multiline) AddTokenRows(panel, binding, row, input, add);
                    apply = panel.Button("Apply");
                }
                finally
                {
                    if (!multiline) panel.EndInline();
                }

                // INLINEAPPLY1: Apply is a game button like Show, Preview and Close Mods (the borrowed steel art).
                UseGameButton(binding, apply);

                LimitServerText(input, row, multiline, displayed);
                // NOPROGMSG1: an empty value stands for a default: the field shows it as a muted hint, never as a value.
                if (!string.IsNullOrEmpty(options.Placeholder)) input.SetPlaceholder(options.Placeholder);
                // NOPROGMSG1: the field's text as typed (each end of an edit), for lines that preview it before Apply.
                SettingDrafts.Note(row.RowId, displayed);
                input.OnChanged(text =>
                {
                    SettingDrafts.Note(row.RowId, text);
                    DraftChanged?.Invoke(row.RowId);
                });
                bool pending = false;
                binding.Status = AdminNote;
                binding.Multiline = multiline;
                void Send(string value)
                {
                    if (pending) return;
                    if (!binding.Enabled)
                    {
                        Plugin.Logger?.LogInfo("Ui: settings " + row.RowId + " refused: " + binding.Why);
                        return;
                    }

                    // TEXTUNDO1 polish: send the value without the spaces the cfg would drop, and show
                    // that in the field now (the server trims too, before its length check).
                    value = UiCommitText.Trim(value);
                    if (!input.IsDisposed && !string.Equals(input.Value, value, StringComparison.Ordinal))
                        input.Value = value;
                    pending = true;
                    binding.Status = WaitingNote;
                    WriteHelp(binding);
                    _requests.Add(ServerSettings.Request(row.RowId, value, displayed, result =>
                    {
                        pending = false;
                        if (input.IsDisposed) return;
                        // TEXTLIMIT1: an accepted value is replaced by what the server stored. On any
                        // refusal (its length, not an admin, a conflict, a timeout, a failed send) the
                        // typed text stays in the field, so nothing typed is lost; the next Apply is
                        // checked against the server's current value, and leaving the page discards it.
                        displayed = result.Success ? result.Value : SafeRead(row);
                        if (result.Success)
                        {
                            LimitServerText(input, row, multiline, displayed);
                            input.Value = displayed;
                        }
                        binding.Status = result.Success ? ServerSavedNote : RefusalNote(result.Error, input.Value, displayed);
                        WriteHelp(binding);
                    }));
                }

                apply.OnClick(() => Send(input.Value));
                binding.Apply = () => Send(input.Value);
                binding.Input = input;
                binding.Handles.Add(apply);
                if (multiline)
                {
                    // TEXTEXPAND1: Done puts the text in the field and sends it, as Apply does.
                    AttachExpand(panel, binding, input, row, options, add,
                        limit: () => ServerTextLimit(row, input.Value),
                        readOnly: false,
                        validate: text => ServerSettings.CheckText(row.RowId, UiCommitText.Trim(text))
                            ?? (ServerSettings.HasDisallowedControl(text) ? ServerSettings.ControlCharactersError : null),
                        done: text =>
                        {
                            if (input.IsDisposed) return;
                            LimitServerText(input, row, multiline: true, text);
                            input.Value = text;
                            Send(text);
                        });
                }
                handle = input;
                break;
            }

            case SettingControl.ReadOnly:
            {
                // A read-only value sits in the control column behind the lock word; why it cannot be
                // changed is the help line's first sentence after the help.
                string prefix = adminOnly ? AdminOnlyPrefix : LockedPrefix;
                handle = panel.Info(caption, () => prefix + ReadOnlyValue(row, options, section), 1);
                // TEXTEXPAND1: a multi-line value the player may not change opens read-only in the large
                // editor, so all of it can be read (the row shows its first line).
                if (ModSettingsLayout.IsTextAreaRow(row, options))
                    AttachExpand(panel, binding, handle, row, options, add,
                        limit: () => 0, readOnly: true, validate: null, done: null);
                break;
            }

            default:
            {
                var input = panel.Input(caption, SafeRead(row));
                LimitText(input, row, multiline: false);
                input.OnChanged(v =>
                {
                    Commit(row, v, trim: true);
                    LimitText(input, row, multiline: false);
                    input.Value = SafeRead(row);
                });
                binding.Sync = () =>
                {
                    LimitText(input, row, multiline: false);
                    input.Value = SafeRead(row);
                };
                handle = input;
                break;
            }
        }

        binding.Handles.Insert(0, handle);
        binding.Main = handle;
        add(handle, RowSetting, (Func<object?>)(() => Describe(row, section, control, options, binding)));

        // The reset gutter: the link sits in this row's gutter and shows while the value differs
        // from its default (and the row is enabled). JOINFLOW1: a date-time row draws its own arrow
        // in its gutter cell (AddDateTime).
        if (Resettable(row) && binding.DateField == null)
        {
            string defaultText = SafeDefault(row);
            var reset = panel.GutterLink(handle, ResetGlyph, ResetTooltipPrefix + (defaultText.Length == 0 ? "(empty)" : defaultText));
            reset.OnClick(() => ResetRow(row));
            var link = reset;
            add(reset, RowSettingReset, (Func<object?>)(() => new Dictionary<string, object?>
            {
                ["id"] = row.RowId,
                ["visible"] = !link.IsDisposed && link.Visible,
                ["state"] = ResetStateName(ResetState(binding)),
                ["tooltip"] = link.Tooltip,
            }));
            binding.Reset = reset;
        }

        _bindings.Add(binding);
        if (binding.Reset != null) PaintReset(binding);
        WatchHover(panel, binding);
    }

    /// <summary>
    /// MENUKIT2: the game draws a resettable row's arrow always — dim at the default (and on a grayed row),
    /// steel blue when changed. A row that can never be reset here has none.
    /// </summary>
    private static SettingsResetState ResetState(ItemBinding binding) =>
        binding.Item.Row is not { } row || !ModSettingsLayout.CanEverReset(row) ? SettingsResetState.None
        : binding.Enabled && ModSettingsLayout.CanReset(row) ? SettingsResetState.Changed
        : SettingsResetState.Default;

    internal static string ResetStateName(SettingsResetState state) => state switch
    {
        SettingsResetState.Changed => "changed",
        SettingsResetState.Default => "default",
        _ => "none",
    };

    private static void PaintReset(ItemBinding binding)
    {
        if (binding.Reset is not { IsDisposed: false } reset) return;
        try { reset.SetResetMark(ResetState(binding)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings reset mark threw: " + e.Message); }
    }

    /// <summary>Rows that can ever carry a reset link: a local write path, or a keybind.</summary>
    private static bool Resettable(SettingsRow row) => ModSettingsLayout.CanEverReset(row);

    private static void Choices(SettingsRow row, out List<string> values, out List<string> labels) =>
        Choices(row, out values, out labels, out _);

    /// <summary>The row's choices, their labels and (OPTIONHOVER1) their explanations, index-aligned.</summary>
    private static void Choices(SettingsRow row, out List<string> values, out List<string> labels, out List<string> descriptions)
    {
        var choices = ModSettingsLayout.ChoicesFor(row);
        var about = ModSettingsLayout.ChoiceDescriptionsFor(row);
        values = new List<string>(choices.Count);
        labels = new List<string>(choices.Count);
        descriptions = new List<string>(choices.Count);
        for (int i = 0; i < choices.Count; i++)
        {
            values.Add(choices[i].Value);
            labels.Add(choices[i].Label);
            descriptions.Add(i < about.Count ? about[i] : "");
        }
    }

    /// <summary>The name a row is drawn under: its display name, with a number's units after it.</summary>
    internal static string Caption(SettingsRow row) => Caption(row, null, ModSettingsLayout.ControlFor(row));

    internal static string Caption(SettingsRow row, ModSettingOptions? options, SettingControl control)
    {
        string label = ModSettingsLayout.DisplayNameOf(row, options);
        if (control == SettingControl.Number || control == SettingControl.Slider)
        {
            var spec = ModSettingsLayout.NumberFor(row);
            if (spec.Units.Length > 0) label += " (" + spec.Units + ")";
        }

        return label;
    }

    /// <summary>The help line of a stage 1 row with no options (tests; the page writes <see cref="ModSettingsLayout.HelpText"/>).</summary>
    internal static string NoteText(SettingsRow row, string? status) =>
        ModSettingsLayout.HelpText(row, null, ModSettingsLayout.ControlFor(row), null, status);

    private void WriteHelp(ItemBinding binding)
    {
        string line;
        var item = binding.Item;
        if (item.Kind == ModSettingsItemKind.Setting && item.Row != null)
            line = ModSettingsLayout.HelpText(item.Row, item.Options, binding.Control, binding.Why, binding.Status,
                scopeWhy: binding.AdminOnly || binding.ServerPartGreyed);
        else
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(item.Options.Help)) parts.Add(ModSettingsLayout.Sentence(item.Options.Help));
            if (binding.Why.Length > 0) parts.Add(ModSettingsLayout.Sentence(binding.Why));
            if (!string.IsNullOrWhiteSpace(binding.Status)) parts.Add(ModSettingsLayout.Sentence(binding.Status));
            line = string.Join(" ", parts);
        }

        binding.HelpLine = line;
        binding.HelpWritten = true;
        if (binding.Main is { IsDisposed: false } main) main.SetHelp(line);
        if (item.Kind == ModSettingsItemKind.Setting && item.Row != null && binding.Main is { IsDisposed: false } chipRow)
            chipRow.SetRowChip(ChipFor(ModSettingsRowMarks.ChipTextOf(item.Row, item.Options), OmitServerChip));
        else if (item.Admin is { } admin && binding.Main is { IsDisposed: false } adminRow)
            adminRow.SetRowChip(ChipFor(ModSettingsRowMarks.AdminChipText(admin), OmitServerChip));
        if ((DescriptionChanged != null || _panel != null) && string.Equals(Description().RowId, item.Id, StringComparison.Ordinal)) NotifyDescription();
    }

    private void Commit(SettingsRow row, string? text, bool trim = false)
    {
        var binding = Find(row.RowId);
        if (binding != null && !binding.Enabled)
        {
            // A grayed row does not take input; a commit that still reaches it (a runtime that
            // cannot gray, the bridge) is refused and the control re-read.
            Plugin.Logger?.LogInfo("Ui: settings " + row.PluginId + " " + row.Label + " refused: " + binding.Why);
            Refresh(row, binding.Why);
            return;
        }

        // TMP raises onEndEdit on Enter and again on the focus loss that follows, and a stepper or
        // selector can land on the value already stored. Nothing is written or logged for a commit
        // that repeats the stored value; the row is only re-read. Compared before the trim below, so a
        // focus-and-blur with no edit never rewrites a stored value that has edge spaces.
        if (SameAsStored(row, text))
        {
            Refresh(row);
            return;
        }

        // TEXTUNDO1 polish: typed text (a field, a text area, the editor's Done) loses its leading and
        // trailing spaces here, before the length check, because the cfg read drops them anyway
        // (UiCommitText); the row's Sync then shows the trimmed value, so what the field shows after the
        // commit is what is stored. A choice (a selector's value) is written as it is.
        if (trim && (row.ValueType == null || row.ValueType == typeof(string)))
        {
            text = UiCommitText.Trim(text);
            if (SameAsStored(row, text))
            {
                Refresh(row);
                return;
            }
        }

        SettingWriteResult result;
        try { result = SettingsCatalog.Submit(row, text); }
        catch (Exception e)
        {
            result = SettingWriteResult.Failed(SafeRead(row), e.Message);
        }

        bool ok = result.State == SettingWriteState.Accepted || result.State == SettingWriteState.Pending;
        // TEXTAREA1: values go through the audit log's cleaner (escaped, one line, cut at 120
        // characters), so a multi-line or pasted value cannot forge log lines or bloat the log.
        if (ok)
            Plugin.Logger?.LogInfo("Ui: settings " + row.PluginId + " " + row.Label + " = "
                + Progenitor.Install.SettingsAuditLog.Clean(SafeRead(row)));
        else
            Plugin.Logger?.LogWarning(
                "Ui: settings " + row.PluginId + " " + row.Label + " rejected '"
                + Progenitor.Install.SettingsAuditLog.Clean(text) + "': " + result.Error);
        AfterChange(row, ok ? SavedNote : result.Error);
    }

    /// <summary>A committed change (or a refusal): re-read the row, then re-evaluate the page's predicates.</summary>
    private void AfterChange(SettingsRow row, string? status)
    {
        Select(row.RowId);
        Refresh(row, status);
        Reevaluate();
    }

    private void ResetRow(SettingsRow row)
    {
        var binding = Find(row.RowId);
        if (binding != null && !binding.Enabled)
        {
            Plugin.Logger?.LogInfo("Ui: settings " + row.PluginId + " " + row.Label + " reset refused: " + binding.Why);
            return;
        }

        // The dim arrow: already at the default, nothing to do.
        if (!ModSettingsLayout.CanReset(row)) return;

        var result = ModSettingsLayout.Reset(row);
        bool ok = result.State == SettingWriteState.Accepted || result.State == SettingWriteState.Pending;
        Plugin.Logger?.LogInfo(
            "Ui: settings " + row.PluginId + " " + row.Label
            + (ok ? " reset to " + Progenitor.Install.SettingsAuditLog.Clean(SafeRead(row)) : " reset refused: " + result.Error));
        AfterChange(row, ok ? "" : result.Error);
    }

    private ItemBinding? Find(string id)
    {
        for (int i = 0; i < _bindings.Count; i++)
            if (string.Equals(_bindings[i].Item.Id, id, StringComparison.Ordinal)) return _bindings[i];
        return null;
    }

    /// <summary>Re-read one row into its control, help line and reset link, in place.</summary>
    internal void Refresh(SettingsRow row, string? status = null)
    {
        for (int i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            if (!string.Equals(binding.Item.Id, row.RowId, StringComparison.Ordinal)) continue;
            try { binding.Sync(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings refresh threw: " + e.Message); }
            if (binding.Control != SettingControl.ServerRequest) binding.Status = status ?? "";
            WriteHelp(binding);
            if (binding.Reset is { IsDisposed: false } reset)
            {
                SetVisible(reset, binding.Visible && ResetShown(binding));
                PaintReset(binding);
            }
        }
    }

    /// <summary>True when <paramref name="text"/> parses to the value already stored (text compared verbatim).</summary>
    internal static bool SameAsStored(SettingsRow row, string? text)
    {
        string stored = SafeRead(row);
        if (row.ValueType == null || row.ValueType == typeof(string))
            return string.Equals(stored, text ?? "", StringComparison.Ordinal);
        return SettingsEditors.TryParse(text, row.ValueType, out var a, out _)
            && SettingsEditors.TryParse(stored, row.ValueType, out var b, out _)
            && Equals(a, b);
    }

    private static bool IsTrue(string? text) => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);

    private static double ParseNumber(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static string SafeRead(SettingsRow row)
    {
        try { return row.Read(); }
        catch { return ""; }
    }

    /// <summary>
    /// TEXTAREA1: the field's character limit (<see cref="ModSettingsLayout.TextFieldLimit"/>): the
    /// author's cap or the framework default, raised to the stored value's length so a longer value
    /// already in the cfg is shown whole and never cut by the field.
    /// </summary>
    private static void LimitText(IUiHandle field, SettingsRow row, bool multiline, string? shown = null)
    {
        int? authorMax = null;
        try { authorMax = row.ToDescriptor().MaxLength; } catch { /* default cap */ }
        int current = Math.Max(SafeRead(row).Length, shown?.Length ?? 0);
        field.SetMaxLength(ModSettingsLayout.TextFieldLimit(authorMax, multiline, current));
        // TEXTFILTER1: a number shown in a plain field (an admin's server-request row, a number
        // setting without a number control) types like a number box.
        if (!multiline)
        {
            UiCharFilter filter = UiCharFilter.None;
            try { filter = UiCharFilters.ForValueType(row.ValueType); } catch { /* free text */ }
            if (filter != UiCharFilter.None) field.SetCharFilter(filter);
        }
    }

    /// <summary>TEXTLIMIT1: what the admin's help line says after a refusal; the kept text is named so it can be discarded.</summary>
    internal const string KeptNote = "Your text is kept: Apply sends it again, or leave the page to discard it.";

    internal static string RefusalNote(string? error, string? typed, string? server)
    {
        string reason = string.IsNullOrWhiteSpace(error) ? "The request failed." : error!.Trim();
        return string.Equals(typed ?? "", server ?? "", StringComparison.Ordinal) ? reason : reason + " " + KeptNote;
    }

    /// <summary>TEXTAREA1's field limit for a local text row (<see cref="LimitText"/>'s number).</summary>
    private static int TextLimit(SettingsRow row, bool multiline, string? shown)
    {
        int? authorMax = null;
        try { authorMax = row.ToDescriptor().MaxLength; } catch { /* default cap */ }
        int current = Math.Max(SafeRead(row).Length, shown?.Length ?? 0);
        return ModSettingsLayout.TextFieldLimit(authorMax, multiline, current);
    }

    /// <summary>
    /// TEXTLIMIT1: an admin's server-wide text field holds what the server accepts
    /// (<see cref="ServerSettings.EffectiveTextLimit"/>: the declared limit, at most the packet's
    /// 2,048), raised to the shown value's length so a longer stored value is never cut by the field.
    /// </summary>
    private static int ServerTextLimit(SettingsRow row, string? shown) =>
        Math.Max(ServerSettings.EffectiveTextLimit(row.RowId), Math.Max(SafeRead(row).Length, shown?.Length ?? 0));

    private static void LimitServerText(IUiHandle field, SettingsRow row, bool multiline, string? shown)
    {
        if (row.ValueType != typeof(string))
        {
            LimitText(field, row, multiline, shown);
            return;
        }

        field.SetMaxLength(ServerTextLimit(row, shown));
    }

    // ---- TEXTEXPAND1: the expanded editor --------------------------------------------------------

    /// <summary>Readback key of a text row's expand button.</summary>
    internal const string RowSettingExpand = "page-setting-expand";

    /// <summary>TOKENINSERT1: the readback row kinds of a field's Insert rows and its Players see line.</summary>
    internal const string RowSettingInsert = "page-setting-insert";

    /// <inheritdoc cref="RowSettingInsert"/>
    internal const string RowSettingPreview = "page-setting-preview";

    /// <summary>TOKENINSERT1: how often the examples and the preview are re-read (a changed draft or countdown repaints).</summary>
    internal const double TokenRefreshSeconds = 0.5;

    /// <summary>
    /// TOKENINSERT1 (requested 2026-10-06): under a field that takes placeholders (<see cref="Progenitor.Networking.TextTokens"/>),
    /// its Insert rows (a key-cap button per token, <c>Countdown · 7d</c>, wrapped to the value column) and its Players see
    /// line (the field's text rendered as players get it, links drawn as they see them). A button inserts its token at the
    /// field's caret (the end when it never had focus) and keeps the keyboard in the field. The examples and the preview
    /// are re-read every <see cref="TokenRefreshSeconds"/> and written only when they changed, so an idle page costs a
    /// short render and a string compare per field per tick.
    /// </summary>
    private void AddTokenRows(IUiPanel panel, ItemBinding binding, SettingsRow row, IUiInput input, Action<IUiHandle, string, object?> add)
    {
        var field = Progenitor.Networking.TextTokens.For(row.RowId);
        if (field == null) return;
        string rowId = row.RowId;
        try
        {
            string Typed() => SettingDrafts.ForRow(rowId) ?? (input.IsDisposed ? SafeRead(row) : input.Value);

            // LISTING1: a counter line right under the field (the server list's room), muted, never a hover target.
            IUiCellRow? counterRow = null;
            string? counted = Progenitor.Networking.TextTokens.Counter(field, Typed());
            if (counted != null)
            {
                counterRow = panel.CellRow(ConfigurationPane.StatusRowSpec(Progenitor.Networking.TextTokens.CounterRowKey(rowId),
                    Progenitor.Networking.TextTokens.CounterLabel, counted));
                binding.Handles.Add(counterRow);
            }

            var captions = new List<string>(field.Tokens.Count);
            foreach (var t in field.Tokens) captions.Add(Progenitor.Networking.TextTokens.Caption(field, t));
            float available = Math.Max(160f, StatusWrapEstimateWidth - ConfigurationPane.StatusLabelWidth - HeadBlockLayout.RightColumnInset - 40f);
            var groups = Progenitor.Networking.TextTokens.PackInsertRows(captions, available);
            var insertRows = new List<(IUiCellRow Row, List<int> Indices)>();
            for (int r = 0; r < groups.Count; r++)
            {
                var spec = ConfigurationPane.InsertRowSpec(Progenitor.Networking.TextTokens.InsertRowKey(rowId, r), r == 0, groups[r], captions);
                var cellRow = panel.CellRow(spec);
                insertRows.Add((cellRow, groups[r]));
                binding.Handles.Add(cellRow);
                foreach (int i in groups[r])
                {
                    var tip = field.Tokens[i];
                    cellRow.OnClick(Progenitor.Networking.TextTokens.InsertCellKey(i), () => InsertToken(binding, row, input, tip.Token));
                }
            }

            string shown = Progenitor.Networking.TextTokens.Preview(field, Typed());
            var preview = panel.CellRow(ConfigurationPane.PreviewRowSpec(Progenitor.Networking.TextTokens.PreviewRowKey(rowId), shown, field.Links));
            binding.Handles.Add(preview);

            void Paint()
            {
                if (preview.IsDisposed) return;
                if (counterRow is { IsDisposed: false } && Progenitor.Networking.TextTokens.Counter(field, Typed()) is { } count
                    && !string.Equals(count, counted, StringComparison.Ordinal))
                {
                    counted = count;
                    counterRow.SetCell(ConfigurationPane.CellStatusValue, count);
                }

                string next = Progenitor.Networking.TextTokens.Preview(field, Typed());
                if (!string.Equals(next, shown, StringComparison.Ordinal))
                {
                    shown = next;
                    preview.SetCell(ConfigurationPane.CellStatusValue, next);
                }

                foreach (var (cellRow, indices) in insertRows)
                {
                    if (cellRow.IsDisposed) continue;
                    foreach (int i in indices)
                    {
                        string caption = Progenitor.Networking.TextTokens.Caption(field, field.Tokens[i]);
                        if (string.Equals(caption, captions[i], StringComparison.Ordinal)) continue;
                        captions[i] = caption;
                        cellRow.SetCell(Progenitor.Networking.TextTokens.InsertCellKey(i), caption);
                    }
                }
            }

            ScheduleStatus(preview, Paint, TokenRefreshSeconds);
            Action<string> onDraft = id => { if (string.Equals(id, rowId, StringComparison.Ordinal)) Paint(); };
            DraftChanged += onDraft;
            _requests.Add(new ActionLease(() => DraftChanged -= onDraft));

            foreach (var (cellRow, indices) in insertRows)
            {
                var ids = indices;
                add(cellRow, RowSettingInsert, (Func<object?>)(() =>
                {
                    var buttons = new List<object?>();
                    foreach (int i in ids)
                        buttons.Add(new Dictionary<string, object?>
                        {
                            ["key"] = Progenitor.Networking.TextTokens.InsertCellKey(i),
                            ["name"] = field.Tokens[i].Name,
                            ["token"] = field.Tokens[i].Token,
                            ["example"] = Progenitor.Networking.TextTokens.Example(field, field.Tokens[i]),
                            ["caption"] = captions[i],
                        });
                    return new Dictionary<string, object?> { ["id"] = rowId, ["row"] = cellRow.Key, ["buttons"] = buttons };
                }));
            }

            add(preview, RowSettingPreview, (Func<object?>)(() => new Dictionary<string, object?>
            {
                ["id"] = rowId,
                ["row"] = preview.Key,
                ["text"] = shown,
                ["links"] = field.Links,
                ["lines"] = Progenitor.Networking.TextTokens.PreviewLines,
                ["counter"] = counted,
            }));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings " + rowId + " insert rows threw: " + e.Message); }
    }

    /// <summary>TOKENINSERT1: an Insert button's click: the token at the caret, the draft noted, the preview repainted.</summary>
    private static void InsertToken(ItemBinding binding, SettingsRow row, IUiInput input, string token)
    {
        if (input.IsDisposed || !binding.Enabled) return;
        string value = input.InsertAtCaret(token);
        SettingDrafts.Note(row.RowId, value);
        DraftChanged?.Invoke(row.RowId);
    }

    /// <summary>
    /// TOKENTIPS2: the grid width a wrapping status row's first height estimate assumes; the runtime then fits the row to
    /// the text it measures at the real width (<see cref="CellRowSpec.FitWrap"/>).
    /// </summary>
    internal const float StatusWrapEstimateWidth = 760f;

    private UiDialog? _expandDialog;
    private string _expandId = "";

    /// <summary>The page drawn last (the dev bridge's <c>field_expand</c> / <c>field_apply</c> find rows here).</summary>
    internal static ModSettingsView? Drawn { get; private set; }

    /// <summary>The expanded editor this page opened, while it is on screen.</summary>
    internal UiDialog? ExpandDialog => _expandDialog is { IsOpen: true } open ? open : null;

    /// <summary>
    /// Put the expand button in <paramref name="handle"/>'s corner (a field) or its gutter (a read-only
    /// value) and bind it to the large editor: titled with the row's name, holding
    /// <paramref name="limit"/> characters, read-only when <paramref name="readOnly"/> or the row is
    /// grayed when it opens. Done runs <paramref name="validate"/> (an error keeps the editor open) and
    /// then <paramref name="done"/>; Cancel and Escape discard.
    /// </summary>
    private void AttachExpand(
        IUiPanel panel, ItemBinding binding, IUiHandle handle, SettingsRow row, ModSettingOptions? options,
        Action<IUiHandle, string, object?> add, Func<int> limit, bool readOnly,
        Func<string, string?>? validate, Action<string>? done)
    {
        IUiIconLink link;
        try { link = panel.CornerLink(handle, UiTextExpandLayout.Glyph, UiTextExpandLayout.Tooltip); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings expand button threw: " + e.Message);
            return;
        }

        string title = ModSettingsLayout.DisplayNameOf(row, options);
        binding.Expand = () => OpenExpand(binding, row, title, limit, readOnly || done == null, validate, done);
        link.OnClick(() => binding.Expand?.Invoke());
        binding.Handles.Add(link);
        binding.ExpandLink = link;
        add(link, RowSettingExpand, (Func<object?>)(() => new Dictionary<string, object?>
        {
            ["id"] = row.RowId,
            ["visible"] = !link.IsDisposed && link.Visible,
            ["tooltip"] = UiTextExpandLayout.Tooltip,
            ["readOnly"] = readOnly || done == null || !binding.Enabled,
            ["open"] = ExpandDialog != null && string.Equals(_expandId, row.RowId, StringComparison.Ordinal),
        }));
    }

    private void OpenExpand(
        ItemBinding binding, SettingsRow row, string title, Func<int> limit, bool readOnly,
        Func<string, string?>? validate, Action<string>? done)
    {
        var factory = ModPagePane.DialogFactory;
        if (factory == null)
        {
            // TEXTEXPAND2: never silent; a host that drew the button without a dialog source is a bug to see in the log.
            Plugin.Logger?.LogWarning("Ui: the expanded editor for " + row.RowId + " has no dialog source; nothing opened.");
            return;
        }
        bool locked = readOnly || !binding.Enabled;
        // The field's text when there is one (what the player sees, typed and not yet applied), else the value.
        string text = binding.Input is { IsDisposed: false } input ? input.Value ?? "" : SafeRead(row);
        int max = locked ? 0 : Math.Max(0, limit());
        var content = new DialogContent
        {
            Shape = UiDialogShape.Prompt,
            Title = title,
            InputInitial = text,
            MaxLength = max,
            // The setting's own limit (not raised to a longer stored value), so the counter shows the overflow.
            CounterLimit = locked ? 0 : OwnLimit(binding, row),
            EditorHeight = UiTextExpandLayout.EditorHeight,
            InputReadOnly = locked,
            Counter = true,
            MainCaption = locked ? "" : UiTextExpandLayout.DoneCaption,
            MainTone = CellTone.Gold,
            CancelCaption = locked ? UiTextExpandLayout.CloseCaption : UiTextExpandLayout.CancelCaption,
            RowKey = UiTextExpandLayout.DialogTag + "-buttons",
            // TOKENTIPS2: the field's Placeholders under the editor too.
            InputNote = Progenitor.Networking.TextTokens.Line(row.RowId),
        };
        try { _expandDialog?.Close(); } catch { /* */ }
        _expandId = row.RowId;
        var dialog = UiDialog.OpenContent(factory, content, UiTextExpandLayout.DialogTag, UiTextExpandLayout.DialogKind, row.RowId);
        if (!locked && done != null) dialog.OnSubmit(done, validate);
        _expandDialog = dialog;
    }

    /// <summary>TEXTEXPAND1: the row's own character limit, before any raise to a longer stored value.</summary>
    private static int OwnLimit(ItemBinding binding, SettingsRow row)
    {
        // SERVERDESC1: a row that counts against a smaller, changing room (the server-list description, less
        // its signature) shows that room in the counter; the field still stops at the row's own limit.
        if (binding.Item.Options.CounterLimit is { } counter)
        {
            try
            {
                int room = counter();
                if (room > 0) return room;
            }
            catch { /* fall back to the row's limit */ }
        }

        if (binding.Control == SettingControl.ServerRequest) return ServerSettings.EffectiveTextLimit(row.RowId);
        int? authorMax = null;
        try { authorMax = row.ToDescriptor().MaxLength; } catch { /* default cap */ }
        return ModSettingsLayout.TextFieldLimit(authorMax, multiline: true, 0);
    }

    /// <summary>TEXTEXPAND1 (bridge): the drawn row captioned <paramref name="label"/> (display name or cfg caption).</summary>
    private ItemBinding? FindByCaption(string? label)
    {
        string want = (label ?? "").Trim();
        if (want.Length == 0) return null;
        foreach (var b in _bindings)
        {
            if (b.Item.Admin is { } admin && string.Equals(admin.Label, want, StringComparison.OrdinalIgnoreCase)) return b;
            if (b.Item.Row is not { } row) continue;
            if (string.Equals(ModSettingsLayout.DisplayNameOf(row, b.Item.Options), want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Label, want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Key, want, StringComparison.OrdinalIgnoreCase))
                return b;
        }

        return null;
    }

    /// <summary>
    /// TEXTEXPAND1 (bridge <c>field_expand</c>): press the expand button of the row captioned
    /// <paramref name="label"/>, as a click does. Throws <see cref="ArgumentException"/> when no such
    /// row has one.
    /// </summary>
    internal Dictionary<string, object?> ExpandForBridge(string? label)
    {
        var b = FindByCaption(label) ?? throw new ArgumentException("no settings row captioned '" + label + "' on the drawn page");
        if (b.Expand == null) throw new ArgumentException("the row '" + label + "' has no expand button (not a text area)");
        b.Expand();
        var open = ExpandDialog;
        return new Dictionary<string, object?>
        {
            ["ok"] = open != null,
            ["row"] = b.Item.Row?.RowId ?? "",
            ["open"] = open != null,
            ["readOnly"] = open?.Content.InputReadOnly ?? false,
            ["maxLength"] = open?.Content.MaxLength ?? 0,
            ["counter"] = open?.CounterText ?? "",
            ["handler"] = "the expand button's click (ModSettingsView.OpenExpand)",
        };
    }

    /// <summary>
    /// TEXTLIMIT1 (bridge <c>field_apply</c>): press the Apply button of the admin row captioned
    /// <paramref name="label"/> with the field's current text.
    /// </summary>
    internal Dictionary<string, object?> ApplyForBridge(string? label) => ApplyForBridge(label, null);

    /// <summary>
    /// FIELDAPPLY1: <paramref name="text"/> (when given) goes in the field as the player's typing would, then the Apply
    /// button's own handler runs: a change that needs a confirmation opens it and sends nothing until it is answered. The
    /// readback says whether a dialog opened (<c>dialog</c>).
    /// </summary>
    internal Dictionary<string, object?> ApplyForBridge(string? label, string? text)
    {
        var b = FindByCaption(label) ?? throw new ArgumentException("no settings row captioned '" + label + "' on the drawn page");
        if (text != null)
        {
            if (b.Input is not { IsDisposed: false } typed)
                throw new ArgumentException("the row '" + label + "' has no field to type into");
            typed.Value = text;
        }
        if (b.Apply == null) throw new ArgumentException("the row '" + label + "' has no Apply button (not an administrator's server-wide row)");
        // JOINFLOW1: an administrator's date or choice control sends each change itself and has no
        // Apply button; pressing a stand-in with the unchanged value would send nothing and wait out the timeout.
        if (b.Editor.Length > 0)
            throw new ArgumentException("the row '" + label + "' is an administrator's " + b.Editor
                + " control: it sends each change itself (no Apply). Change it on the control, or use the setting op's request.");
        string value = b.Input?.Value ?? "";
        b.Apply();
        var d = ApplyStatusForBridge(label);
        d["sentLength"] = b.Item.Admin is { WriteOnly: true } ? -1 : value.Length;
        d["handler"] = "the Apply button's click";
        // FIELDAPPLY1: a confirmation the Apply opened (nothing is sent until it is answered).
        d["confirmation"] = AdminConfirm != null;
        d["dialog"] = UiDialog.Describe();
        return d;
    }

    /// <summary>TEXTLIMIT1 (bridge): the admin row's state — waiting, saved by the server, or the refusal — and its value.</summary>
    internal Dictionary<string, object?> ApplyStatusForBridge(string? label)
    {
        var b = FindByCaption(label) ?? throw new ArgumentException("no settings row captioned '" + label + "' on the drawn page");
        string status = b.Status ?? "";
        // A write-only row (the password) is never read back, not even as a hash.
        string field = b.Item.Admin is { WriteOnly: true } ? "" : b.Input is { IsDisposed: false } input ? input.Value ?? "" : "";
        string stored = b.Item.Row is { } row ? SafeRead(row) : b.Item.Admin is { } admin ? admin.SafeRead() ?? "" : "";
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["row"] = b.Item.Row?.RowId ?? b.Item.Admin?.SettingId ?? "",
            ["status"] = status,
            ["waiting"] = string.Equals(status, WaitingNote, StringComparison.Ordinal),
            ["saved"] = string.Equals(status, ServerSavedNote, StringComparison.Ordinal),
            ["fieldLength"] = field.Length,
            ["fieldLineBreaks"] = CountBreaks(field),
            ["fieldHash"] = UiTextTestInput.Hash(field),
            ["storedLength"] = stored.Length,
            ["storedLineBreaks"] = CountBreaks(stored),
            ["storedHash"] = UiTextTestInput.Hash(stored),
            ["helpLine"] = b.HelpLine,
        };
    }

    private static int CountBreaks(string s)
    {
        int n = 0;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    // ---- JOINFLOW1: the date-time field, and an administrator's date and choice controls ----

    private static bool IsServerWide(ModSettingsSection? section) => section?.Scope == SettingScope.ServerWide;

    /// <summary>The zone a date-time row is drawn in: its zone function, else the player's (personal) or UTC (server-wide).</summary>
    private static TimeZoneInfo DateZone(ModSettingOptions? options, ModSettingsSection? section) =>
        ModSettingsLayout.DateTimeZone(options, IsServerWide(section) ? TimeZoneInfo.Utc : TimeZoneInfo.Local);

    /// <summary>The zone line's "who": <c>Your time</c> on a personal row, <c>Server time</c> on a server-wide one with a zone.</summary>
    private static string DateWho(ModSettingOptions? options, ModSettingsSection? section) =>
        !IsServerWide(section) ? UiDateTimeLayout.YourTime
        : options?.DateTimeZone != null ? UiDateTimeLayout.ServerTime
        : UiDateTimeLayout.ZoneTime;

    private static UiDateTimeField NewDateField(
        IUiPanel panel, ModSettingsSection section, SettingsRow row, ModSettingOptions options, string caption, bool resettable)
    {
        var field = UiDateTimeField.Create(
            panel, caption, SafeRead(row), DateZone(options, section), ModSettingsLayout.DateTimePrecision(options), resettable);
        field.Who = DateWho(options, section);
        // Read when drawn and when the picker opens; the registration drops the plugin's functions on unload.
        field.ZoneSource = () => DateZone(options, section);
        field.SecondsSource = () => ModSettingsLayout.DateTimePrecision(options) == DateTimePickerPrecision.Second;
        return field;
    }

    /// <summary>A personal date-time row: Done commits the ISO UTC value through <see cref="Commit"/>.</summary>
    private IUiHandle AddDateTime(
        IUiPanel panel, ItemBinding binding, ModSettingsSection section, SettingsRow row, ModSettingOptions options, string caption)
    {
        var field = NewDateField(panel, section, row, options, caption, resettable: Resettable(row));
        field.OnChanged(iso =>
        {
            Commit(row, iso);
            if (!field.IsDisposed) field.Value = SafeRead(row);
        });
        field.SetReset(() => ResetRow(row));
        binding.DateField = field;
        binding.Sync = () =>
        {
            field.Value = SafeRead(row);
            field.PaintReset(ResetState(binding));
        };
        field.PaintReset(ResetState(binding));
        return field;
    }

    /// <summary>
    /// The administrator's control for a row that is not a text field: the date-time field, the choice the
    /// builder asked for, or the number stepper. Null keeps the text field and Apply.
    /// </summary>
    private IUiHandle? AddServerControl(
        IUiPanel panel, ItemBinding binding, ModSettingsSection section, SettingsRow row, ModSettingOptions options, string caption)
    {
        if (ModSettingsLayout.IsDateTimeRow(row, options)) return AddServerDateTime(panel, binding, section, row, options, caption);
        if (ModSettingsLayout.IsTextAreaRow(row, options)) return null;
        // SERVERDESC1: an administrator's switch is a switch; each flip sends a request (was a text field + Apply).
        // Review fix (live: "Progenitor signature" drew as a text field reading "true"): a server-owned row's Kind is
        // ReadOnly whatever its type, so the entry's value type decides.
        if (row.Kind == SettingsEditorKind.Bool || row.ValueType == typeof(bool)) return AddServerToggle(panel, binding, row, caption);
        if (ModSettingsLayout.ChoicesFor(row).Count > 0) return AddServerChoice(panel, binding, row, options, caption);
        // A number keeps its field and Apply: a stepper sending one request per step would spend the
        // server's edit rate limit (10 a minute) on a single change from 10 to 25.
        return null;
    }

    private void PrepareServer(ItemBinding binding, SettingsRow row, string editor)
    {
        binding.Editor = editor;
        binding.Status = AdminNote;
        binding.ServerShown = SafeRead(row);
    }

    /// <summary>An administrator's date-time row: Done sends the ISO UTC value to the server.</summary>
    private IUiHandle AddServerDateTime(
        IUiPanel panel, ItemBinding binding, ModSettingsSection section, SettingsRow row, ModSettingOptions options, string caption)
    {
        var field = NewDateField(panel, section, row, options, caption, resettable: false);
        PrepareServer(binding, row, "datetime");
        binding.DateField = field;
        void Shown(bool ok, string shown) { if (!field.IsDisposed) field.Value = shown; }
        field.OnChanged(iso => SendServer(binding, row, iso, Shown));
        binding.Apply = () => SendServer(binding, row, field.Value, Shown);
        binding.Sync = () => SyncServer(binding, row, v => field.Value = v);
        return field;
    }

    /// <summary>An administrator's finite choice: the radio or selector the builder asked for; each pick sends a request.</summary>
    private IUiHandle AddServerChoice(
        IUiPanel panel, ItemBinding binding, SettingsRow row, ModSettingOptions options, string caption)
    {
        Choices(row, out var values, out var labels, out var descriptions);
        string current = SafeRead(row);
        IUiHandle control;
        Func<string> read;
        Action<string> write;
        string editor;
        if (options.Control == SettingControl.Radio && values.Count <= ModSettingsLayout.MaxListOptions)
        {
            var radio = panel.Radio(caption, values, current, labels, descriptions);
            radio.OnChanged(v => SendServer(binding, row, v, (_, shown) => { if (!radio.IsDisposed) radio.Value = shown; }));
            control = radio;
            read = () => radio.Value;
            write = v => radio.Value = v;
            editor = "radio";
        }
        else
        {
            var dropdown = panel.Dropdown(caption, values, current, labels, descriptions);
            dropdown.OnChanged(v => SendServer(binding, row, v, (_, shown) => { if (!dropdown.IsDisposed) dropdown.Value = shown; }));
            control = dropdown;
            read = () => dropdown.Value;
            write = v => dropdown.Value = v;
            editor = options.Control is not (SettingControl.Dropdown or SettingControl.Radio) && UiOptionsRules.FitsSegmented(labels)
                ? "segmented"
                : "dropdown";
        }

        PrepareServer(binding, row, editor);
        binding.Apply = () => SendServer(binding, row, read(), (_, shown) => { if (!control.IsDisposed) write(shown); });
        binding.Sync = () => SyncServer(binding, row, write);
        return control;
    }

    /// <summary>SERVERDESC1: an administrator's bool row: a switch whose flip sends <c>true</c> / <c>false</c> to the server.</summary>
    private IUiHandle AddServerToggle(IUiPanel panel, ItemBinding binding, SettingsRow row, string caption)
    {
        var toggle = panel.Toggle(caption, IsTrue(SafeRead(row)));
        void Show(string shown)
        {
            if (toggle.IsDisposed) return;
            toggle.Value = IsTrue(shown);
            toggle.Text = caption;
        }

        toggle.OnChanged(v => SendServer(binding, row, v ? "true" : "false", (_, shown) => Show(shown)));
        PrepareServer(binding, row, "toggle");
        binding.Apply = () => SendServer(binding, row, toggle.Value ? "true" : "false", (_, shown) => Show(shown));
        binding.Sync = () => SyncServer(binding, row, Show);
        return toggle;
    }

    /// <summary>
    /// SERVERDESC1: the text an administrator has typed in row <paramref name="rowId"/> of the page drawn last and
    /// not sent yet: the open expanded editor's text when it belongs to that row, else the row's field. Null when
    /// the row is not drawn with a field (another page, or a read-only row).
    /// </summary>
    internal static string? TypedText(string rowId)
    {
        var view = Drawn;
        if (view == null || string.IsNullOrEmpty(rowId)) return null;
        try
        {
            if (view.ExpandDialog is { } open && string.Equals(view._expandId, rowId, StringComparison.Ordinal))
                return open.Text ?? "";
            foreach (var b in view._bindings)
            {
                if (b.Item.Row is not { } row || !string.Equals(row.RowId, rowId, StringComparison.Ordinal)) continue;
                return b.Input is { IsDisposed: false } input ? input.Value ?? "" : null;
            }
        }
        catch { /* a page being rebuilt reads as not drawn */ }

        return null;
    }

    /// <summary>A synced value arrived: show it, unless a request is on its way (its answer decides).</summary>
    private static void SyncServer(ItemBinding binding, SettingsRow row, Action<string> write)
    {
        if (binding.RequestPending) return;
        binding.ServerShown = SafeRead(row);
        if (binding.Main is { IsDisposed: true }) return;
        write(binding.ServerShown);
    }

    /// <summary>
    /// Send an administrator's value with the existing request path (<see cref="ServerSettings.Request(string, string, string, Action{ServerSettingResult})"/>),
    /// with the help-line status Apply uses: waiting, <c>Saved by the server.</c>, or the refusal. A value equal to
    /// the shown one sends nothing; a change while a request is on its way, or on a grayed row, is put back.
    /// <paramref name="shown"/> is called with the value the control should show afterwards.
    /// </summary>
    private void SendServer(ItemBinding binding, SettingsRow row, string value, Action<bool, string> shown)
    {
        string current = binding.ServerShown ?? SafeRead(row);
        if (binding.RequestPending || !binding.Enabled)
        {
            Plugin.Logger?.LogInfo("Ui: settings " + row.RowId + " refused: "
                + (binding.RequestPending ? "a request is already waiting for the server" : binding.Why));
            if (!binding.RequestPending) shown(false, current);
            return;
        }

        if (string.Equals(value ?? "", current, StringComparison.Ordinal)) return;
        binding.RequestPending = true;
        binding.Status = WaitingNote;
        WriteHelp(binding);
        _requests.Add(ServerSettings.Request(row.RowId, value ?? "", current, result =>
        {
            binding.RequestPending = false;
            binding.ServerShown = result.Success ? result.Value : SafeRead(row);
            if (binding.Main is { IsDisposed: true }) return;
            binding.Status = result.Success
                ? ServerSavedNote
                : string.IsNullOrWhiteSpace(result.Error) ? "The request failed." : result.Error.Trim();
            try { shown(result.Success, binding.ServerShown); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings server control update threw: " + e.Message); }
            WriteHelp(binding);
        }));
    }

    /// <summary>A read-only value as the control column shows it: a date-time row as its wall-clock time (the player's zone unless the row names one).</summary>
    private static string ReadOnlyValue(SettingsRow row, ModSettingOptions? options, ModSettingsSection? section)
    {
        string value = SafeRead(row);
        // NOPROGMSG1: a read-only copy of a row whose empty value stands for a default shows that default.
        if (value.Trim().Length == 0 && !string.IsNullOrEmpty(options?.EmptyShows)) return options!.EmptyShows!;
        if (value.Trim().Length == 0) return NotSetValue;
        if (!ModSettingsLayout.IsDateTimeRow(row, options)) return value;
        return UiDateTimeLayout.DisplayValue(
            value, ModSettingsLayout.DateTimeZone(options, TimeZoneInfo.Local), ModSettingsLayout.DateTimePrecision(options));
    }

    /// <summary>The readback of a date-time row: <c>control: datetime</c> when drawn as the picker, <c>iso</c>, <c>display</c>, <c>zone</c>, <c>seconds</c>.</summary>
    private static void AddDateTimeReadback(
        Dictionary<string, object?> d, SettingsRow row, ModSettingsSection? section, SettingControl control,
        ModSettingOptions? options, ItemBinding? binding)
    {
        string iso = SafeRead(row);
        var field = binding?.DateField as UiDateTimeField;
        var zone = field?.Zone
            ?? (control == SettingControl.ReadOnly ? ModSettingsLayout.DateTimeZone(options, TimeZoneInfo.Local) : DateZone(options, section));
        var precision = ModSettingsLayout.DateTimePrecision(options);
        var utc = ResetCountdown.TryParseUtc(iso, out var parsed) ? parsed : DateTime.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        if (field != null) d["control"] = "datetime";
        d["iso"] = iso;
        d["display"] = UiDateTimeLayout.DisplayValue(iso, zone, precision);
        d["zone"] = DateTimePickerModel.ZoneCaption(zone, local, field?.Who ?? DateWho(options, section));
        d["seconds"] = precision == DateTimePickerPrecision.Second;
        d["pickerOpen"] = field?.IsOpen ?? false;
        if (field != null && ModSettingsLayout.CanEverReset(row) && control == SettingControl.DateTime)
            d["reset"] = ResetStateName(field.ResetState);
    }

    /// <summary>An administrator's date or choice control: <c>editor</c>, <c>requestPending</c>, and its choices.</summary>
    private static void AddEditorReadback(Dictionary<string, object?> d, SettingsRow row, ItemBinding binding)
    {
        d["editor"] = binding.Editor;
        d["requestPending"] = binding.RequestPending;
        if (binding.Editor is "dropdown" or "segmented" or "radio")
        {
            var choices = ModSettingsLayout.ChoicesFor(row);
            var values = new List<string>(choices.Count);
            for (int i = 0; i < choices.Count; i++) values.Add(choices[i].Value);
            d["choices"] = values;
        }
    }

    /// <summary>JOINFLOW1 (bridge <c>datetime_open</c>): the date-time field of the drawn row captioned <paramref name="label"/>.</summary>
    internal UiDateTimeField? DateTimeFieldForBridge(string? label) => FindByCaption(label)?.DateField as UiDateTimeField;

    /// <summary>JOINFLOW1 (bridge): why the row captioned <paramref name="label"/> is grayed, or empty.</summary>
    internal string WhyForBridge(string? label) => FindByCaption(label)?.Why ?? "";

    private static string SafeDefault(SettingsRow row)
    {
        try { return row.DefaultValue; }
        catch { return ""; }
    }

    private static string SafeBounds(SettingsRow row)
    {
        try { return row.Bounds; }
        catch { return ""; }
    }

    /// <summary>What the page drew for one item, and its current state.</summary>
    private sealed class ItemBinding
    {
        internal ItemBinding(ModSettingsItem item, ModSettingsSection section, SettingControl control)
        {
            Item = item;
            Section = section;
            Control = control;
        }

        internal ModSettingsItem Item { get; }
        internal ModSettingsSection Section { get; }
        internal SettingControl Control { get; set; }

        /// <summary>Every widget the item drew except its reset link, which has its own rule. The first is <see cref="Main"/>.</summary>
        internal List<IUiHandle> Handles { get; } = new();

        /// <summary>PENDING1: a status row's line as last painted, and whether it was in gold.</summary>
        internal string? StatusText { get; set; }

        internal CellTone StatusToneNow { get; set; } = CellTone.Muted;

        /// <summary>INLINEAPPLY1: the row's action buttons drawn as game buttons (an inline Apply), for the readback.</summary>
        internal List<IUiButton> InlineButtons { get; } = new();

        /// <summary>TEXTEXPAND1: opens the large editor for this row (a text area, or a read-only multi-line value).</summary>
        internal Action? Expand { get; set; }

        /// <summary>TEXTEXPAND1: the expand button.</summary>
        internal IUiIconLink? ExpandLink { get; set; }

        /// <summary>TEXTLIMIT1: an administrator row's Apply (sends the field's text).</summary>
        internal Action? Apply { get; set; }

        /// <summary>The row's text field or text area, when it has one.</summary>
        internal IUiInput? Input { get; set; }

        /// <summary>TEXTLIMIT1: an administrator row drawn as a text area.</summary>
        internal bool Multiline { get; set; }

        /// <summary>JOINFLOW1: the row's date-time field (a personal or an administrator's date-time row).</summary>
        internal IUiDateTimeField? DateField { get; set; }

        /// <summary>
        /// JOINFLOW1: the control an administrator's row is drawn with when it is not a text field
        /// (<c>datetime</c>, <c>dropdown</c>, <c>segmented</c>, <c>radio</c>, <c>number</c>); empty otherwise.
        /// </summary>
        internal string Editor { get; set; } = "";

        /// <summary>JOINFLOW1: an administrator's request is on its way (the control's next change waits for it).</summary>
        internal bool RequestPending { get; set; }

        /// <summary>An admin row drawn read-only (<c>Admin only · value</c>) for a player who is not an admin there.</summary>
        internal bool BuiltViewOnly { get; set; }

        /// <summary>JOINFLOW1: the server's value the administrator's control last showed (the request's expected value).</summary>
        internal string? ServerShown { get; set; }

        /// <summary>The row whose caption carries the help line.</summary>
        internal IUiHandle? Main { get; set; }

        internal IUiIconLink? Reset { get; set; }

        internal Action Sync { get; set; } = () => { };

        internal bool Visible { get; set; } = true;

        internal bool ShownByIf { get; set; } = true;

        internal bool Enabled { get; set; } = true;

        internal bool ServerPartGreyed { get; set; }

        /// <summary>The grayed row's reason alone; the fallback is read back separately (the help line joins them).</summary>
        internal string ServerPartReason { get; set; } = "";

        /// <summary>Settings scopes: a Server-wide row drawn read-only for a player who is not an admin.</summary>
        internal bool AdminOnly { get; set; }

        /// <summary>The joined server's lock list entry that keeps this row read-only, or null.</summary>
        internal string? LockedBy { get; set; }

        /// <summary>Settings scopes: the last availability decision (state, code, reason).</summary>
        internal SettingAvailabilityResult Availability { get; set; } = new(SettingRowState.Editable, "", "");

        internal string Why { get; set; } = "";

        internal string Status { get; set; } = "";

        internal string HelpLine { get; set; } = "";

        internal bool HelpWritten { get; set; }
    }
}

/// <summary>SETTINGS1: the words of the settings section's dialogs.</summary>
internal static class ModSettingsCopy
{
    internal const string ResetAllYes = "Yes, reset";

    internal static string ResetAllHeading(string? modName) =>
        "Reset " + (string.IsNullOrWhiteSpace(modName) ? "this mod" : modName!.Trim()) + " to its defaults?";

    internal static string ResetAllBody(int count) =>
        (count == 1 ? "1 setting goes" : count + " settings go")
        + " back to the value the mod ships with. Server-owned settings are not changed.";
}

/// <summary>
/// NOPROGMSG1: the text a server setting's field holds as typed (each end of an edit), before Apply, by row id. Lines that
/// preview a message read it (<see cref="ForRow"/>). Game-free; only this generation's open pages write it.
/// </summary>
internal static class SettingDrafts
{
    private static readonly Dictionary<string, string> Drafts = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    internal static void Note(string rowId, string? text)
    {
        if (string.IsNullOrEmpty(rowId)) return;
        lock (Gate) Drafts[rowId] = text ?? "";
    }

    /// <summary>The field's text for <paramref name="rowId"/>, or null when no page drew that field.</summary>
    internal static string? ForRow(string rowId)
    {
        lock (Gate) return Drafts.TryGetValue(rowId ?? "", out var text) ? text : null;
    }

    internal static void ResetForTests()
    {
        lock (Gate) Drafts.Clear();
    }
}

/// <summary>An <see cref="IDisposable"/> that runs one action once.</summary>
internal sealed class ActionLease : IDisposable
{
    private Action? _dispose;

    internal ActionLease(Action dispose) => _dispose = dispose;

    public void Dispose()
    {
        var a = _dispose;
        _dispose = null;
        a?.Invoke();
    }
}
