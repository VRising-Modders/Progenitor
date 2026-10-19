using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// SETTINGS1 stage 2 (docs/mod-settings.md): the layout of a mod's settings on its page, bound to
/// BepInEx <see cref="ConfigEntryBase"/> objects. Get one from <c>ctx.Settings.Page(page =&gt; …)</c>.
/// Every method returns the builder, so a page reads as one chain:
///
/// <code>
/// ctx.Settings.Page(page => page
///     .Section("Combat")
///         .Toggle(showHints, help: "Show the combat hints.")
///         .Slider(volume)
///         .Radio(difficulty)
///     .Section("Server")
///         .Number(limit, scope: SettingScope.Server)
///         .Toggle(experimental, needsRestart: true, showIf: () => advanced.Value));
/// </code>
///
/// <para>The entries stay the source of truth: the page reads them when it is drawn and writes a
/// change through the same validated path as every settings row. The builder runs once, when
/// <c>Page</c> is called; the predicates, the button actions and the info functions it was given are
/// called later, from the page, and are dropped when the plugin unloads.</para>
///
/// <para><b>Common options</b> (every setting method): <c>label</c> replaces the name derived from the
/// cfg key; <c>help</c> is the line under the name (the entry's description otherwise);
/// <c>showIf</c> / <c>enableIf</c> hide or gray the row, re-evaluated after every change on the page;
/// <c>needsRestart</c> marks the row <c>Pending restart</c> once its value differs from the value when
/// the mod loaded; <c>needsServerPart</c> grays the row with <c>fallback</c> while the mod's server part
/// is not known to run on the joined server; <c>scope</c> declares the value local or server-set
/// (<see cref="SettingScope.Server"/> also opts the synced entry into administrator edits).</para>
/// </summary>
public sealed partial class SettingsPageBuilder
{
    private readonly string _pluginId;
    private readonly List<PageSectionSpec> _sections = new();
    private int _anonymous;

    internal SettingsPageBuilder(string pluginId) => _pluginId = pluginId ?? "";

    internal IReadOnlyList<PageSectionSpec> Sections => _sections;

    /// <summary>Start a section: a heading, then the rows added after it. Rows added before any section go in an untitled one.</summary>
    public SettingsPageBuilder Section(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A section title is required.", nameof(title));
        _sections.Add(new PageSectionSpec(title.Trim()));
        return this;
    }

    /// <summary>
    /// Core only (LISTING1): what the section added last is about, for the description column while its heading is
    /// hovered (before, the heading showed whichever setting the column last described).
    /// </summary>
    internal SettingsPageBuilder SectionHelp(string help)
    {
        if (_sections.Count == 0) throw new InvalidOperationException("Add a section before SectionHelp().");
        ModSettingsSectionHelp.Set(_sections[_sections.Count - 1].Title, help);
        return this;
    }

    /// <summary>A setting drawn with the control its type and bounds choose (as a stage 1 row).</summary>
    public SettingsPageBuilder Setting(
        ConfigEntryBase entry, string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, null, null, label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>A switch for a <see cref="bool"/> entry.</summary>
    public SettingsPageBuilder Toggle(
        ConfigEntry<bool> entry, string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.Toggle, null, label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>
    /// A number field with steppers. <paramref name="min"/>, <paramref name="max"/> and
    /// <paramref name="step"/> bound it where the entry declares nothing; <paramref name="units"/>
    /// follows the name.
    /// </summary>
    public SettingsPageBuilder Number(
        ConfigEntryBase entry, double? min = null, double? max = null, double? step = null, string? units = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.Number, NumberPresentation(SettingsEditorHint.Numeric, min, max, step, units),
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>
    /// A slider with an exact number box. Needs both bounds (an <c>AcceptableValueRange</c> or
    /// <paramref name="min"/> and <paramref name="max"/>); without them the row is a number field.
    /// </summary>
    public SettingsPageBuilder Slider(
        ConfigEntryBase entry, double? min = null, double? max = null, double? step = null, string? units = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.Slider, NumberPresentation(SettingsEditorHint.Slider, min, max, step, units),
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>
    /// A selector for an enum, an <c>AcceptableValueList</c> or <paramref name="choices"/> (with
    /// <paramref name="labels"/> drawn in their place). Always a selector, however short the list.
    /// </summary>
    public SettingsPageBuilder Dropdown(
        ConfigEntryBase entry, IReadOnlyList<string>? choices = null, IReadOnlyList<string>? labels = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.Dropdown, ChoicePresentation(SettingsEditorHint.Dropdown, choices, labels),
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>
    /// Every option visible at once: a segmented picker for two to four short options, a vertical
    /// list otherwise. Takes the same finite sets as <see cref="Dropdown"/>.
    /// </summary>
    public SettingsPageBuilder Radio(
        ConfigEntryBase entry, IReadOnlyList<string>? choices = null, IReadOnlyList<string>? labels = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.Radio, ChoicePresentation(null, choices, labels),
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>
    /// A set of choices: a <c>[Flags]</c> enum's single-bit values, or — for a <see cref="string"/>
    /// entry — <paramref name="choices"/>, stored comma-separated (<c>"a, b"</c>). A string entry
    /// without choices falls back to a text field.
    /// </summary>
    public SettingsPageBuilder MultiSelect(
        ConfigEntryBase entry, IReadOnlyList<string>? choices = null, IReadOnlyList<string>? labels = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.MultiSelect, ChoicePresentation(null, choices, labels),
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>A text field; <paramref name="multiline"/> makes it <paramref name="lines"/> lines tall (Enter starts a new line).</summary>
    public SettingsPageBuilder Text(
        ConfigEntry<string> entry, bool multiline = false, int lines = 3,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null)
    {
        AddEntry(entry, multiline ? SettingControl.TextArea : SettingControl.Text,
            new SettingPresentation { EditorHint = SettingsEditorHint.Text },
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);
        Last!.Options.Lines = lines < 2 ? 2 : lines > 12 ? 12 : lines;
        return this;
    }

    /// <summary>A color swatch with a hex field (<c>#RRGGBB</c>, or <c>#RRGGBBAA</c> with <paramref name="alpha"/>).</summary>
    public SettingsPageBuilder Color(
        ConfigEntry<string> entry, bool alpha = false,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null) =>
        AddEntry(entry, SettingControl.Color, new SettingPresentation { EditorHint = SettingsEditorHint.Color, HasAlpha = alpha },
            label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);

    /// <summary>
    /// JOINFLOW1: a date and time picker for a <see cref="string"/> entry that holds ISO 8601 UTC
    /// (<c>2026-10-10T18:00:00Z</c>, empty for none). The value is shown and edited as a wall-clock time in
    /// the zone <paramref name="zone"/> returns (read when the row is drawn and when the picker opens; without
    /// it the player's own zone for a personal row, UTC for a server-wide one), with the offset shown.
    /// <paramref name="seconds"/> is read when the picker opens: true adds a seconds stepper. On a synced
    /// entry declared <c>scope: SettingScope.Server</c>, an administrator's Done sends the value to the
    /// server; everyone else sees it read-only as a wall-clock time.
    /// </summary>
    public SettingsPageBuilder DateTime(
        ConfigEntry<string> entry, Func<bool>? seconds = null, Func<TimeZoneInfo>? zone = null,
        string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsRestart = false, bool needsServerPart = false, string? fallback = null, SettingScope? scope = null)
    {
        AddEntry(entry, SettingControl.DateTime, null, label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);
        Last!.Options.DateTimeSeconds = seconds;
        Last.Options.DateTimeZone = zone;
        return this;
    }

    /// <summary>
    /// The capture control for keybind <paramref name="id"/>, registered with <c>ctx.Keybinds</c>
    /// (it moves out of the automatic Keys section). An id nothing registered draws nothing.
    /// </summary>
    public SettingsPageBuilder Keybind(
        string id, string? label = null, string? help = null, Func<bool>? showIf = null, Func<bool>? enableIf = null,
        bool needsServerPart = false, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A keybind id is required.", nameof(id));
        var item = new PageItemSpec(ModSettingsItemKind.Setting)
        {
            KeybindId = id.Trim(),
            Options = Options(label, help, showIf, enableIf, false, needsServerPart, fallback, null),
        };
        Current.Items.Add(item);
        return this;
    }

    /// <summary>
    /// A button that runs <paramref name="action"/> on the main thread. With <paramref name="confirm"/>
    /// the click first asks that question in the kit dialog and only <b>Yes</b> runs it. An action
    /// that throws is logged; the page is not closed.
    /// </summary>
    public SettingsPageBuilder Button(
        string caption, Action action, string? confirm = null, string? help = null, Func<bool>? showIf = null,
        Func<bool>? enableIf = null, bool needsServerPart = false, string? fallback = null, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(caption)) throw new ArgumentException("A button caption is required.", nameof(caption));
        if (action == null) throw new ArgumentNullException(nameof(action));
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Button)
        {
            Label = caption.Trim(),
            Action = action,
            Confirm = confirm,
            // label: the row's name on the left; the caption is on the button. Defaults to the caption.
            Options = Options(label, help, showIf, enableIf, false, needsServerPart, fallback, null),
        });
        return this;
    }

    /// <summary>
    /// A read-only line: <paramref name="label"/> and the text <paramref name="value"/> returns, re-read
    /// every <paramref name="refreshSeconds"/> (at least 0.25; 0 reads once) while the page is shown. A
    /// function that throws is logged once and the last value stays.
    /// </summary>
    public SettingsPageBuilder Info(
        string label, Func<string> value, double refreshSeconds = 1, string? help = null, Func<bool>? showIf = null)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("An info label is required.", nameof(label));
        if (value == null) throw new ArgumentNullException(nameof(value));
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Info)
        {
            Label = label.Trim(),
            Value = value,
            RefreshSeconds = refreshSeconds,
            Options = Options(null, help, showIf, null, false, false, null, null),
        });
        return this;
    }

    /// <summary>
    /// JOINFLOW1: draw the button or info line added last under <b>Server settings</b> (with
    /// <see cref="SettingScope.ServerWide"/>) instead of <b>Your settings</b>, next to the server-wide rows it
    /// belongs with. Anyone can still press or read it; only setting rows are admin-only. Throws when the last
    /// item is not a button or info line.
    /// </summary>
    public SettingsPageBuilder InScope(SettingScope scope)
    {
        var last = Last ?? throw new InvalidOperationException("Add a button or info line before InScope().");
        if (last.Kind is not (ModSettingsItemKind.Button or ModSettingsItemKind.Info))
            throw new InvalidOperationException("InScope() applies to a button or info line; a setting row's scope comes from its entry.");
        last.Options.Scope = scope == SettingScope.ServerWide ? SettingScope.ServerWide : null;
        return this;
    }

    /// <summary>
    /// Settings scopes: the row added last is <b>hidden</b>, instead of grayed with the framework's reason,
    /// while the half of the mod it needs is absent or unreachable — not connected, a server without
    /// Progenitor, or one without this mod's server part (for a Server-wide row, or a row marked
    /// <c>needsServerPart</c>). Use it for a setting that makes no sense on its own. A wait for the server, a
    /// version mismatch and the row's own <c>enableIf</c> still gray it. Throws when no row was added yet or
    /// the last one is a divider.
    /// </summary>
    public SettingsPageBuilder HideWhenAlone()
    {
        var last = Last ?? throw new InvalidOperationException("Add a row before HideWhenAlone().");
        if (last.Kind == ModSettingsItemKind.Divider)
            throw new InvalidOperationException("HideWhenAlone() applies to a setting, button or info line, not a divider.");
        last.Options.WhenAlone = AloneBehavior.Hide;
        return this;
    }

    /// <summary>
    /// The longest value, in characters, the <see cref="Text"/> row added last accepts. The field
    /// stops typing and paste there and a longer write is refused. Without it a text field holds
    /// 256 characters and a text area 4,096 (a value already longer is kept whole). Throws when the
    /// last row is not a text row or <paramref name="characters"/> is below 1.
    /// </summary>
    public SettingsPageBuilder MaxLength(int characters)
    {
        if (characters < 1) throw new ArgumentOutOfRangeException(nameof(characters), "A text row holds at least one character.");
        var last = Last ?? throw new InvalidOperationException("Add a Text row before MaxLength().");
        if (last.Options.Control is not (SettingControl.Text or SettingControl.TextArea))
            throw new InvalidOperationException("MaxLength() applies to a Text row.");
        last.Presentation ??= new SettingPresentation { EditorHint = SettingsEditorHint.Text };
        last.Presentation.MaxLength = characters;
        return this;
    }

    /// <summary>
    /// OPTIONHOVER1: a short explanation of each choice of the <see cref="Dropdown"/>, <see cref="Radio"/> or automatic
    /// choice row added last, index-aligned with its choices (the <c>choices</c> given, else the enum's names or the
    /// <c>AcceptableValueList</c>'s values, in order); an empty entry has none. A radio list draws each under its option; the
    /// details pane shows the option's explanation while the pointer is over it (a dropdown's open list, a radio line).
    /// Throws when the last row is not a setting that can be a choice, or when the row lists its <c>choices</c> and the
    /// count differs.
    /// </summary>
    public SettingsPageBuilder OptionDescriptions(params string[] descriptions)
    {
        if (descriptions == null) throw new ArgumentNullException(nameof(descriptions));
        var last = Last ?? throw new InvalidOperationException("Add a Dropdown or Radio row before OptionDescriptions().");
        if (last.Kind != ModSettingsItemKind.Setting || last.Entry == null
            || last.Options.Control is not (null or SettingControl.Dropdown or SettingControl.Radio or SettingControl.Segmented))
            throw new InvalidOperationException("OptionDescriptions() applies to a Dropdown, Radio or automatic choice row.");
        if (last.Presentation?.Choices is { } choices && choices.Length != descriptions.Length)
            throw new ArgumentException("Give one description per choice.", nameof(descriptions));
        last.Presentation ??= new SettingPresentation();
        last.Presentation.ChoiceDescriptions = Copy(descriptions);
        return this;
    }

    /// <summary>
    /// SERVERDESC1 (Core only): the expanded editor of the text row added last counts against
    /// <paramref name="limit"/> (read when the editor opens) instead of the row's own limit, so a text that
    /// shares a fixed total with something else shows the room it really has. Typing still stops at
    /// <see cref="MaxLength"/>. Throws when the last row is not a text row.
    /// </summary>
    internal SettingsPageBuilder EditorCounterLimit(Func<int> limit)
    {
        if (limit == null) throw new ArgumentNullException(nameof(limit));
        var last = Last ?? throw new InvalidOperationException("Add a Text row before EditorCounterLimit().");
        if (last.Options.Control is not (SettingControl.Text or SettingControl.TextArea))
            throw new InvalidOperationException("EditorCounterLimit() applies to a Text row.");
        last.Options.CounterLimit = limit;
        return this;
    }

    /// <summary>
    /// Show the row added last only while <paramref name="condition"/> holds. Unlike a <c>showIf</c> delegate the rule
    /// is data: a published setting carries it to clients without the mod's DLL, which evaluate it against the values
    /// the server sent. Combined with the row's <c>showIf</c> (both must hold); calling it twice requires both rules.
    /// Throws when no row was added yet or the last one is a divider.
    /// </summary>
    public SettingsPageBuilder ShowWhen(SettingCondition condition)
    {
        if (condition == null) throw new ArgumentNullException(nameof(condition));
        var last = RuleTarget(nameof(ShowWhen));
        var previous = last.Options.ShowIf;
        last.Options.ShowWhen = last.Options.ShowWhen == null ? condition : last.Options.ShowWhen & condition;
        last.Options.ShowIf = previous == null ? () => Holds(condition) : () => previous() && Holds(condition);
        return this;
    }

    /// <summary>As <see cref="ShowWhen"/>, for graying the row (<c>enableIf</c>).</summary>
    public SettingsPageBuilder EnableWhen(SettingCondition condition)
    {
        if (condition == null) throw new ArgumentNullException(nameof(condition));
        var last = RuleTarget(nameof(EnableWhen));
        var previous = last.Options.EnableIf;
        last.Options.EnableWhen = last.Options.EnableWhen == null ? condition : last.Options.EnableWhen & condition;
        last.Options.EnableIf = previous == null ? () => Holds(condition) : () => previous() && Holds(condition);
        return this;
    }

    /// <summary>Shorthand for <c>ShowWhen(SettingCondition.Is(setting, value))</c>.</summary>
    public SettingsPageBuilder ShowIf(ConfigEntryBase setting, object value) => ShowWhen(SettingCondition.Is(setting, value));

    /// <summary>Shorthand for <c>EnableWhen(SettingCondition.Is(setting, value))</c>.</summary>
    public SettingsPageBuilder EnableIf(ConfigEntryBase setting, object value) => EnableWhen(SettingCondition.Is(setting, value));

    private PageItemSpec RuleTarget(string method)
    {
        var last = Last ?? throw new InvalidOperationException("Add a row before " + method + "().");
        if (last.Kind == ModSettingsItemKind.Divider)
            throw new InvalidOperationException(method + "() applies to a setting, button or info line, not a divider.");
        return last;
    }

    /// <summary>Local evaluation of an authored rule: the bound entry, else this plugin's entry or received value by key.</summary>
    private bool Holds(SettingCondition condition) =>
        condition.Evaluate(c => ScopedSettingsRows.LocalValue(_pluginId, c), id => ScopedSettingsRows.ServerFact(_pluginId, id));

    /// <summary>
    /// A published setting placed by row id (<c>PerPlayer</c> settings and item lists; the typed overloads in
    /// <c>ModSettingsPage.Player.cs</c>). Drawn while the joined server provides it; nothing otherwise.
    /// </summary>
    internal SettingsPageBuilder AddScoped(
        string rowId, SettingControl? control, SettingPresentation? presentation, string? label, string? help,
        Func<bool>? showIf, Func<bool>? enableIf)
    {
        if (string.IsNullOrWhiteSpace(rowId)) throw new ArgumentException("A setting id is required.", nameof(rowId));
        var options = Options(label, help, showIf, enableIf, false, false, null, null);
        options.Control = control;
        options.ScopedRowId = rowId;
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Setting) { Presentation = presentation, Options = options });
        return this;
    }

    /// <summary>The number presentation of a typed overload (min, max, step, units).</summary>
    internal static SettingPresentation NumberHints(bool slider, double? min, double? max, double? step, string? units) =>
        NumberPresentation(slider ? SettingsEditorHint.Slider : SettingsEditorHint.Numeric, min, max, step, units);

    /// <summary>The choice presentation of a typed overload.</summary>
    internal static SettingPresentation? ChoiceHints(bool dropdown, IReadOnlyList<string>? choices, IReadOnlyList<string>? labels) =>
        ChoicePresentation(dropdown ? SettingsEditorHint.Dropdown : null, choices, labels);

    /// <summary>A thin rule between groups of rows in the current section.</summary>
    public SettingsPageBuilder Divider()
    {
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Divider));
        return this;
    }

    // ---- Settings organization (Core only): Progenitor's own page --------------------------------------

    /// <summary>
    /// Core only: the bound entry <c>[<paramref name="section"/>] <paramref name="key"/></c> of this page's plugin,
    /// found when the page is drawn (<see cref="SettingsCatalog.FindEntry"/>), so the row needs no
    /// <see cref="ConfigEntryBase"/> when the page is registered: the module that owns the entry may bind it later. A
    /// key no module has bound draws nothing. <paramref name="owned"/>: the entry is in Progenitor's own shared cfg,
    /// which the chainloader sweep lists as another plugin's source; the row is editable all the same.
    /// </summary>
    internal SettingsPageBuilder SettingRef(
        string section, string key, string? label = null, string? help = null, SettingControl? control = null, bool owned = false,
        Func<bool>? showIf = null, bool needsRestart = false)
    {
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A section and a key are required.");
        var options = Options(label, help, showIf, null, needsRestart, false, null, null);
        options.Control = control;
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Setting)
        {
            RefSection = section.Trim(),
            RefKey = key.Trim(),
            RefOwned = owned,
            Options = options,
        });
        return this;
    }

    /// <summary>
    /// Core only: every keybind of this page's plugin that no <see cref="Keybind"/> item places, in the order the
    /// catalog lists them, drawn here (rather than in the automatic Keys section at the page's end).
    /// </summary>
    internal SettingsPageBuilder AllKeybinds()
    {
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Setting) { AllKeybinds = true });
        return this;
    }

    /// <summary>
    /// Core only (settings organization): a link row keyed <paramref name="key"/> (<see cref="SettingsLinks"/>): the
    /// sentence <paramref name="text"/> and <b>Open</b>, which runs <paramref name="open"/>. Readback <c>page-link
    /// {target, landing, label}</c>; the bridge clicks <c>&lt;key&gt;/open</c>.
    /// </summary>
    internal SettingsPageBuilder Link(string key, string text, Action open, string target, string? help = null, SettingScope? scope = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A link key is required.", nameof(key));
        if (open == null) throw new ArgumentNullException(nameof(open));
        var options = Options(null, help, null, null, false, false, null, null);
        // Three homes: Progenitor's page's link to the Server tab is drawn under On this server.
        if (scope == SettingScope.ServerWide) options.Scope = SettingScope.ServerWide;
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Link)
        {
            Label = text ?? "",
            Action = open,
            LinkKey = key.Trim(),
            LinkTarget = target ?? "",
            Options = options,
        });
        return this;
    }

    /// <summary>
    /// Core only (three homes): the item added last is not counted as a setting of its group — a drawer's row count
    /// and its more-than-four threshold leave it out (a fold button, a note for non-admins).
    /// </summary>
    internal SettingsPageBuilder Uncounted()
    {
        var last = Last ?? throw new InvalidOperationException("Add an item before Uncounted().");
        last.Options.Uncounted = true;
        return this;
    }

    /// <summary>
    /// Core only (settings organization stage 5): a row whose value is the joined server's, read from and sent through
    /// the admin channel (<paramref name="spec"/>), drawn under <b>Server settings</b>. <paramref name="showIf"/> is the
    /// admin rule (decision 5: hidden from a player who is not an admin there).
    /// </summary>
    internal SettingsPageBuilder AdminSetting(AdminSettingSpec spec, Func<bool>? showIf = null)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Key)) throw new ArgumentException("An admin setting needs a key.", nameof(spec));
        var options = Options(spec.Label, spec.Help, showIf, null, false, false, null, null);
        options.Scope = SettingScope.ServerWide;
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Admin) { Label = spec.Label, Admin = spec, Options = options });
        return this;
    }

    /// <summary>
    /// Core only (PENDING1): draw the info line added last as a status row (the muted info-row look, not a setting), its
    /// line in gold while <paramref name="attention"/> is true, and its hover/help text from <paramref name="help"/>.
    /// </summary>
    internal SettingsPageBuilder Status(Func<bool> attention, Func<string>? help = null)
    {
        if (attention == null) throw new ArgumentNullException(nameof(attention));
        return StatusTone(() => attention() ? CellTone.Gold : CellTone.Muted, help);
    }

    /// <summary>Core only: <see cref="Status"/> with any tone (muted, gold, warn).</summary>
    internal SettingsPageBuilder StatusTone(Func<CellTone> tone, Func<string>? help = null)
    {
        var last = Last ?? throw new InvalidOperationException("Add an info line before Status().");
        if (last.Kind != ModSettingsItemKind.Info) throw new InvalidOperationException("Status() applies to an info line.");
        last.Options.StatusTone = tone ?? throw new ArgumentNullException(nameof(tone));
        last.Options.HelpNow = help;
        return this;
    }

    /// <summary>Core only (TOKENTIPS2): the status row added last wraps its line (every line kept) instead of ellipsizing it.</summary>
    internal SettingsPageBuilder StatusWraps() => StatusWraps(0);

    /// <summary>
    /// Core only (RADIODESC1): as <see cref="StatusWraps()"/>, with a fixed height of <paramref name="lines"/> lines for a
    /// line whose text changes while it is shown (a preview); 0 fits the row to its text.
    /// </summary>
    internal SettingsPageBuilder StatusWraps(int lines)
    {
        if (lines < 0) throw new ArgumentOutOfRangeException(nameof(lines));
        StatusWrapsCore();
        Last!.Options.StatusWrapLines = lines;
        return this;
    }

    private void StatusWrapsCore()
    {
        var last = Last ?? throw new InvalidOperationException("Add a status line before StatusWraps().");
        if (last.Kind != ModSettingsItemKind.Info || last.Options.StatusTone == null)
            throw new InvalidOperationException("StatusWraps() applies to a status line (StatusTone first).");
        last.Options.StatusWrap = true;
    }

    /// <summary>
    /// Core only (NOPROGMSG1): the text row added last shows <paramref name="placeholder"/> as a muted hint while empty,
    /// and a read-only copy of it shows <paramref name="emptyShows"/> for an empty value (the default it stands for).
    /// </summary>
    internal SettingsPageBuilder EmptyMeans(string placeholder, string? emptyShows = null)
    {
        var last = Last ?? throw new InvalidOperationException("Add a text setting before EmptyMeans().");
        if (last.Kind != ModSettingsItemKind.Setting) throw new InvalidOperationException("EmptyMeans() applies to a setting.");
        last.Options.Placeholder = placeholder;
        last.Options.EmptyShows = emptyShows ?? placeholder;
        return this;
    }

    /// <summary>Core only: the button added last is drawn in the danger tone.</summary>
    internal SettingsPageBuilder Destructive()
    {
        var last = Last ?? throw new InvalidOperationException("Add a button before Destructive().");
        if (last.Kind != ModSettingsItemKind.Button) throw new InvalidOperationException("Destructive() applies to a button.");
        last.Options.Destructive = true;
        return this;
    }

    /// <summary>
    /// Core only: one more <c>widget_read</c> key on the button or info line added last (<paramref name="key"/>,
    /// <paramref name="value"/> read when the row is read).
    /// </summary>
    internal SettingsPageBuilder Readback(string key, Func<object?> value)
    {
        var last = Last ?? throw new InvalidOperationException("Add a button or info line before Readback().");
        if (last.Kind is not (ModSettingsItemKind.Button or ModSettingsItemKind.Info))
            throw new InvalidOperationException("Readback() applies to a button or info line.");
        last.Options.ExtraReadbackKey = key;
        last.Options.ExtraReadback = value;
        return this;
    }

    private PageSectionSpec Current
    {
        get
        {
            if (_sections.Count == 0) _sections.Add(new PageSectionSpec(""));
            return _sections[_sections.Count - 1];
        }
    }

    private PageItemSpec? Last
    {
        get
        {
            var items = Current.Items;
            return items.Count == 0 ? null : items[items.Count - 1];
        }
    }

    private SettingsPageBuilder AddEntry(
        ConfigEntryBase entry, SettingControl? control, SettingPresentation? presentation, string? label, string? help,
        Func<bool>? showIf, Func<bool>? enableIf, bool needsRestart, bool needsServerPart, string? fallback, SettingScope? scope)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        var options = Options(label, help, showIf, enableIf, needsRestart, needsServerPart, fallback, scope);
        options.Control = control;
        Current.Items.Add(new PageItemSpec(ModSettingsItemKind.Setting)
        {
            Entry = entry,
            Presentation = presentation,
            Options = options,
        });
        return this;
    }

    private static ModSettingOptions Options(
        string? label, string? help, Func<bool>? showIf, Func<bool>? enableIf, bool needsRestart, bool needsServerPart,
        string? fallback, SettingScope? scope) => new()
    {
        Label = string.IsNullOrWhiteSpace(label) ? null : label!.Trim(),
        Help = string.IsNullOrWhiteSpace(help) ? null : help,
        ShowIf = showIf,
        EnableIf = enableIf,
        NeedsRestart = needsRestart,
        NeedsServerPart = needsServerPart,
        Fallback = (fallback ?? "").Trim(),
        Scope = scope,
    };

    private static SettingPresentation NumberPresentation(
        SettingsEditorHint hint, double? min, double? max, double? step, string? units) => new()
    {
        EditorHint = hint,
        Min = Invariant(min),
        Max = Invariant(max),
        Step = step is > 0 ? Invariant(step) : null,
        Units = string.IsNullOrWhiteSpace(units) ? null : units,
    };

    private static SettingPresentation? ChoicePresentation(
        SettingsEditorHint? hint, IReadOnlyList<string>? choices, IReadOnlyList<string>? labels)
    {
        if (hint == null && (choices == null || choices.Count == 0)) return null;
        string[]? values = choices == null || choices.Count == 0 ? null : Copy(choices);
        string[]? names = labels == null || labels.Count == 0 ? null : Copy(labels);
        if (values != null && names != null && names.Length != values.Length)
            throw new ArgumentException("Give one label per choice.", nameof(labels));
        return new SettingPresentation { EditorHint = hint, Choices = values, ChoiceLabels = names };
    }

    private static string[] Copy(IReadOnlyList<string> list)
    {
        var copy = new string[list.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = list[i] ?? "";
        return copy;
    }

    private static string? Invariant(double? value) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value.Value.ToString("R", CultureInfo.InvariantCulture)
            : null;

    internal string NextId(string kind) =>
        _pluginId + "/" + kind + "/" + (_anonymous++).ToString(CultureInfo.InvariantCulture);
}

/// <summary>One builder section before it becomes rows.</summary>
internal sealed class PageSectionSpec
{
    internal PageSectionSpec(string title) => Title = title ?? "";

    internal string Title { get; }

    internal List<PageItemSpec> Items { get; } = new();
}

/// <summary>One builder item: an entry, a keybind id, a button, an info line or a divider.</summary>
internal sealed class PageItemSpec
{
    internal PageItemSpec(ModSettingsItemKind kind) => Kind = kind;

    internal ModSettingsItemKind Kind { get; }

    internal ConfigEntryBase? Entry { get; set; }

    internal SettingPresentation? Presentation { get; set; }

    internal string? KeybindId { get; set; }

    internal ModSettingOptions Options { get; set; } = new();

    internal string Label { get; set; } = "";

    internal Action? Action { get; set; }

    internal string? Confirm { get; set; }

    internal Func<string>? Value { get; set; }

    internal double RefreshSeconds { get; set; }

    internal string Id { get; set; } = "";

    /// <summary>Settings organization: a row placed by its entry's section and key, found when the page is drawn.</summary>
    internal string? RefSection { get; set; }

    internal string? RefKey { get; set; }

    /// <summary>The referenced entry is Progenitor's own (shared cfg): editable although the sweep lists its file as foreign.</summary>
    internal bool RefOwned { get; set; }

    /// <summary>Every keybind of the plugin no <c>Keybind</c> item places.</summary>
    internal bool AllKeybinds { get; set; }

    internal string LinkKey { get; set; } = "";

    internal string LinkTarget { get; set; } = "";

    /// <summary>Settings organization stage 5: an admin-channel row's spec.</summary>
    internal AdminSettingSpec? Admin { get; set; }

    internal void Release()
    {
        Admin = null;
        Action = null;
        Value = null;
        Options.Release();
    }
}

/// <summary>
/// SETTINGS1 stage 2: one plugin's settings page. Disposing it (the plugin's unload does, on every
/// <c>PluginUnloadReason</c>) drops the page, the delegates it holds and the administrator-edit
/// registrations a <c>scope: Server</c> row made, and closes the mod's page if it is on show.
/// </summary>
internal sealed class ModSettingsPageRegistration : IDisposable
{
    private readonly List<IDisposable> _serverEdits = new();
    private readonly HashSet<string> _missingKeybinds = new(StringComparer.Ordinal);

    internal ModSettingsPageRegistration(object owner, string pluginId, IReadOnlyList<PageSectionSpec> sections)
    {
        Owner = owner;
        PluginId = pluginId ?? "";
        SectionSpecs = sections ?? Array.Empty<PageSectionSpec>();
    }

    internal object Owner { get; }

    internal string PluginId { get; }

    internal IReadOnlyList<PageSectionSpec> SectionSpecs { get; }

    internal bool IsDisposed { get; private set; }

    /// <summary>Administrator-edit registrations this page made. Tests.</summary>
    internal int ServerEditCount => _serverEdits.Count;

    internal void AddServerEdit(IDisposable registration) => _serverEdits.Add(registration);

    private readonly List<IDisposable> _textLimits = new();

    /// <summary>TEXTLIMIT1: the text rows' declared limits (<see cref="ServerSettings.DeclareText"/>), withdrawn with the page.</summary>
    internal void AddTextLimit(IDisposable declaration) => _textLimits.Add(declaration);

    /// <summary>
    /// The page's sections as the mod page draws them, with fresh rows read now. A keybind id that
    /// nothing registered is skipped (logged once).
    /// </summary>
    internal List<ModSettingsSection> Sections()
    {
        var result = new List<ModSettingsSection>();
        if (IsDisposed) return result;
        List<SettingsRow>? keybinds = null;
        // Settings organization: the keybinds a Keybind item places are left out of an AllKeybinds item.
        var placedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in SectionSpecs)
            foreach (var it in s.Items)
                if (it.KeybindId != null) placedKeys.Add(it.KeybindId);
        for (int i = 0; i < SectionSpecs.Count; i++)
        {
            var spec = SectionSpecs[i];
            var items = new List<ModSettingsItem>(spec.Items.Count);
            for (int j = 0; j < spec.Items.Count; j++)
            {
                var item = spec.Items[j];
                switch (item.Kind)
                {
                    case ModSettingsItemKind.Setting when item.RefKey != null:
                    {
                        var row = RefRow(item);
                        if (row != null) items.Add(ModSettingsItem.ForRow(row, item.Options));
                        break;
                    }
                    case ModSettingsItemKind.Setting when item.AllKeybinds:
                    {
                        keybinds ??= KeybindRows();
                        foreach (var row in keybinds)
                            if (!placedKeys.Contains(row.Key)) items.Add(ModSettingsItem.ForRow(row));
                        break;
                    }
                    case ModSettingsItemKind.Link when item.Action != null:
                        items.Add(ModSettingsItem.ForLink(item.Id, item.LinkKey, item.Label, item.Action, item.LinkTarget, item.Options));
                        break;
                    case ModSettingsItemKind.Setting when item.Entry != null:
                        // Scope resolves when the page is drawn too: an entry bound synced after
                        // Page() is picked up here.
                        if (item.Options.Scope == SettingScope.Server && item.Options.ScopeMismatch)
                            ModSettingsPages.RegisterServerScope(this, PluginId, item, logMismatch: false);
                        items.Add(ModSettingsItem.ForRow(ModSettingsLayout.RowOf(PluginId, item.Entry, item.Presentation), item.Options));
                        break;
                    case ModSettingsItemKind.Setting when item.Options.ScopedRowId != null:
                        // A published setting placed by id: drawn from the server's definition while it has one.
                        if (ScopedSettingsRows.Placed(item) is { } placed) items.Add(placed);
                        else
                        {
                            // Not provided now: claim the id (this side's cfg row of a server-wide list is not drawn
                            // raw in the automatic sections) and draw nothing.
                            var claim = item.Options.Copy();
                            claim.ClaimOnly = true;
                            claim.ShowIf = () => false;
                            items.Add(ModSettingsItem.ForInfo(item.Id, item.Options.Label ?? item.Id, () => "", 0, claim));
                        }
                        break;
                    case ModSettingsItemKind.Setting when item.KeybindId != null:
                    {
                        keybinds ??= KeybindRows();
                        var row = keybinds.Find(r => string.Equals(r.Key, item.KeybindId, StringComparison.Ordinal));
                        if (row != null) items.Add(ModSettingsItem.ForRow(row, item.Options));
                        else if (_missingKeybinds.Add(item.KeybindId))
                            Plugin.Logger?.LogWarning(
                                "Ui: settings page of " + PluginId + " names keybind '" + item.KeybindId + "', which is not registered; skipped.");
                        break;
                    }
                    case ModSettingsItemKind.Button when item.Action != null:
                        items.Add(ModSettingsItem.ForButton(item.Id, item.Label, item.Action, item.Confirm, item.Options));
                        break;
                    case ModSettingsItemKind.Info when item.Value != null:
                        items.Add(ModSettingsItem.ForInfo(item.Id, item.Label, item.Value, item.RefreshSeconds, item.Options));
                        break;
                    case ModSettingsItemKind.Divider:
                        items.Add(ModSettingsItem.ForDivider(item.Id));
                        break;
                    case ModSettingsItemKind.Admin when item.Admin != null:
                        items.Add(ModSettingsItem.ForAdmin(item.Id, item.Admin, item.Options));
                        break;
                }
            }

            result.Add(new ModSettingsSection(spec.Title, ModSettingsSectionKind.Page, items));
        }

        return result;
    }

    /// <summary>
    /// Settings organization: a referenced entry's row, read now, or null while no module has bound it. Progenitor's
    /// own shared-cfg entry (<see cref="PageItemSpec.RefOwned"/>) is editable; any other follows the catalog's rule.
    /// </summary>
    private SettingsRow? RefRow(PageItemSpec item)
    {
        ConfigEntryBase? entry;
        try { entry = SettingsCatalog.FindEntry(PluginId, item.RefSection!, item.RefKey!); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings page of " + PluginId + " could not find [" + item.RefSection + "] " + item.RefKey + ": " + e.Message);
            return null;
        }

        if (entry == null) return null;
        if (!item.RefOwned) return ModSettingsLayout.RowOf(PluginId, entry, item.Presentation);
        var presentation = ModSettingsLayout.Merge(
            SettingsCatalog.PresentationFor(PluginId, entry.Definition.Section, entry.Definition.Key), item.Presentation);
        return SettingsRow.ForEntry(PluginId, entry, SettingsCatalog.IsSynced(entry), foreign: false, presentation: presentation);
    }

    private List<SettingsRow> KeybindRows()
    {
        var rows = new List<SettingsRow>();
        try
        {
            foreach (var row in SettingsCatalog.Rows())
                if (row.Kind == SettingsEditorKind.Keybind && string.Equals(row.PluginId, PluginId, StringComparison.OrdinalIgnoreCase))
                    rows.Add(row);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings page keybind read threw: " + e.Message); }
        return rows;
    }

    /// <summary>True when any needs-restart row differs from its value at load.</summary>
    internal bool AnyPendingRestart()
    {
        foreach (var section in Sections())
            foreach (var item in section.Items)
                if (item.Row != null && ModSettingsLayout.IsPendingRestart(item.Row, item.Options)) return true;
        return false;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        for (int i = 0; i < _serverEdits.Count; i++)
        {
            try { _serverEdits[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings page server-edit release threw: " + e.Message); }
        }

        _serverEdits.Clear();
        for (int i = 0; i < _textLimits.Count; i++)
        {
            try { _textLimits[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings page text-limit release threw: " + e.Message); }
        }

        _textLimits.Clear();
        for (int i = 0; i < SectionSpecs.Count; i++)
            for (int j = 0; j < SectionSpecs[i].Items.Count; j++) SectionSpecs[i].Items[j].Release();
        ModSettingsPages.Forget(this);
    }
}

/// <summary>
/// SETTINGS1 stage 2: the registered settings pages, one per plugin id. Game-free: the mod page asks
/// it for sections (<see cref="ModsMenu.BuildSettingsSections"/>), <c>ctx.Settings</c> registers
/// through it, and the unload sweep (<see cref="ModsMenu.RemoveAll"/>) drops an owner's pages.
/// </summary>
internal static class ModSettingsPages
{
    private static readonly Dictionary<string, ModSettingsPageRegistration> Pages = new(StringComparer.OrdinalIgnoreCase);

    internal static int Count => Pages.Count;

    /// <summary>Changes whenever a page is registered or dropped (the server re-derives published presentation).</summary>
    internal static int Epoch { get; private set; }

    /// <summary>
    /// Where <paramref name="pluginId"/>'s page places the setting <paramref name="rowId"/>: the first row whose id it is,
    /// its section's title and index, and the row's index on the page (counting every item). False when no page places it.
    /// </summary>
    internal static bool TryPlacement(string pluginId, string rowId, out PageItemSpec item, out string sectionTitle,
        out int sectionOrder, out int rowOrder)
    {
        item = null!; sectionTitle = ""; sectionOrder = -1; rowOrder = -1;
        if (!TryGet(pluginId, out var page) || page == null) return false;
        int n = 0;
        for (int i = 0; i < page.SectionSpecs.Count; i++)
        {
            var section = page.SectionSpecs[i];
            foreach (var it in section.Items)
            {
                if (it.Kind == ModSettingsItemKind.Setting && string.Equals(it.Id, rowId, StringComparison.Ordinal))
                {
                    item = it; sectionTitle = section.Title; sectionOrder = i; rowOrder = n;
                    return true;
                }
                n++;
            }
        }
        return false;
    }

    /// <summary>
    /// Run <paramref name="build"/> once and register its page for <paramref name="pluginId"/>. A
    /// builder that throws registers nothing and the exception reaches the caller. Once per plugin:
    /// a second page while the first is registered throws <see cref="ArgumentException"/>.
    /// </summary>
    internal static ModSettingsPageRegistration Register(object owner, string pluginId, Action<SettingsPageBuilder> build)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("A plugin id is required.", nameof(pluginId));
        if (build == null) throw new ArgumentNullException(nameof(build));
        if (Pages.TryGetValue(pluginId, out var existing) && !existing.IsDisposed)
            throw new ArgumentException("A settings page is already registered for " + pluginId + ".");

        var builder = new SettingsPageBuilder(pluginId);
        build(builder);
        var sections = builder.Sections;
        var registration = new ModSettingsPageRegistration(owner, pluginId, sections);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sections.Count; i++)
        {
            foreach (var item in sections[i].Items)
            {
                string id = item.Kind switch
                {
                    ModSettingsItemKind.Button => pluginId + "/button/" + item.Label,
                    ModSettingsItemKind.Info => pluginId + "/info/" + item.Label,
                    ModSettingsItemKind.Divider => builder.NextId("divider"),
                    ModSettingsItemKind.Link => pluginId + "/link/" + item.LinkKey,
                    ModSettingsItemKind.Admin => item.Admin!.SettingId,
                    _ when item.AllKeybinds => builder.NextId("keybinds"),
                    _ when item.Options.ScopedRowId != null => item.Options.ScopedRowId,
                    _ when item.RefKey != null => pluginId + "/" + item.RefSection + "/" + item.RefKey,
                    _ => item.Entry != null
                        ? pluginId + "/" + item.Entry.Definition.Section + "/" + item.Entry.Definition.Key
                        : pluginId + "/" + Keybinds.Section + "/" + item.KeybindId,
                };

                // Two buttons or info lines with one caption get distinct ids ("…/button/Run#2"). A
                // setting placed twice keeps its id: the page draws it once (the first place).
                if (item.Kind is ModSettingsItemKind.Button or ModSettingsItemKind.Info)
                {
                    string unique = id;
                    for (int n = 2; !ids.Add(unique); n++) unique = id + "#" + n.ToString(CultureInfo.InvariantCulture);
                    id = unique;
                }

                item.Id = id;
                if (item.Entry == null) continue;

                // TEXTLIMIT1: the text row's declared limit, on both sides. The server checks every
                // administrator edit against it (its own copy, never the client's); the client's
                // admin field and its pre-check use the same numbers.
                if (item.Entry.SettingType == typeof(string) && item.Options.Control is SettingControl.Text or SettingControl.TextArea)
                    registration.AddTextLimit(ServerSettings.DeclareText(
                        id, item.Presentation?.MaxLength, item.Options.Control == SettingControl.TextArea));

                if (item.Options.Control == SettingControl.MultiSelect
                    && item.Entry.SettingType == typeof(string)
                    && SettingsBounds.Describe(item.Entry).StartsWith("one of: ", StringComparison.Ordinal))
                {
                    Plugin.Logger?.LogWarning(
                        "Ui: settings page of " + pluginId + " asks for a multi-select on " + id
                        + ", whose AcceptableValueList accepts one value; it is drawn as its normal control.");
                }

                // Needs-restart compares against the value the mod loaded with: this call is the load.
                if (item.Options.NeedsRestart)
                {
                    try { item.Options.LoadValue = SettingsEditors.Format(item.Entry.BoxedValue); }
                    catch { item.Options.LoadValue = null; }
                }

                if (item.Options.Scope == SettingScope.Server) RegisterServerScope(registration, pluginId, item);
            }
        }

        // RESEND1 (live 2026-10-14): a needs-server-part row turns on only when the joined server's handshake request names
        // the mod, and the server part names itself only by declaring itself. Said once per load, here, where the author
        // looks, instead of rows that stay gray with no hint why.
        if (ServerPartUndeclared(owner, sections))
            Plugin.Logger?.LogWarning("Ui: settings page of " + pluginId + " has needsServerPart rows, but the plugin has no "
                + "[NetworkCompatibility]: unless its server part calls ctx.Handshake.DeclareSelf() (or RequireSelf()), the "
                + "server's handshake request never names it and those rows stay grayed on every server.");

        Pages[pluginId] = registration;
        Epoch++;
        ModsMenu.NotifySettingsPageChanged(pluginId);
        return registration;
    }

    /// <summary>
    /// RESEND1: the page marks rows <c>needsServerPart</c> but the plugin type declares no
    /// <see cref="Progenitor.Plugins.NetworkCompatibilityAttribute"/>, so nothing makes its server part name itself in the
    /// handshake request (the only evidence those rows read). A plugin that calls <c>DeclareSelf</c> itself is fine; this
    /// cannot see that, so it is a warning, never a refusal.
    /// </summary>
    internal static bool ServerPartUndeclared(object owner, IReadOnlyList<PageSectionSpec> sections)
    {
        bool needs = false;
        for (int i = 0; i < sections.Count && !needs; i++)
            foreach (var item in sections[i].Items)
                if (item.Options.NeedsServerPart) { needs = true; break; }
        if (!needs) return false;
        try { return System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Progenitor.Plugins.NetworkCompatibilityAttribute>(owner.GetType(), inherit: true) == null; }
        catch { return false; }
    }

    /// <summary>
    /// A <c>scope: Server</c> row goes through the administrator request path: its synced entry is
    /// opted into server edits (<see cref="ServerSettings.Register"/>) unless the mod already did
    /// (<c>ctx.Menu.AllowServerEdits</c>). An entry that is not bound synced cannot be server-set; that
    /// is logged and the row keeps its own (local) path.
    /// </summary>
    internal static void RegisterServerScope(
        ModSettingsPageRegistration registration, string pluginId, PageItemSpec item, bool logMismatch = true)
    {
        var entry = item.Entry!;
        string rowId = pluginId + "/" + entry.Definition.Section + "/" + entry.Definition.Key;
        bool synced;
        try { synced = ConfigSync.IsRegistered(pluginId, entry); }
        catch { synced = false; }
        if (!synced)
        {
            item.Options.ScopeMismatch = true;
            if (logMismatch)
                Plugin.Logger?.LogWarning(
                    "Ui: settings page of " + pluginId + " declares " + rowId
                    + " server-set, but the entry is not bound synced yet; it stays local until it is. Bind it"
                    + " synced before ctx.Settings.Page (the server opts it into edits only then); the client"
                    + " page retries each time it is drawn.");
            return;
        }

        item.Options.ScopeMismatch = false;
        if (ServerSettings.IsEditable(rowId)) return;
        try
        {
            registration.AddServerEdit(ServerSettings.Register(pluginId, entry));
            item.Options.ServerEditsRegistered = true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings page of " + pluginId + " could not opt " + rowId + " into server edits: " + e.Message);
        }
    }

    internal static bool TryGet(string? pluginId, out ModSettingsPageRegistration? registration)
    {
        registration = null;
        if (string.IsNullOrEmpty(pluginId)) return false;
        if (!Pages.TryGetValue(pluginId!, out var found) || found.IsDisposed) return false;
        registration = found;
        return true;
    }

    internal static bool Has(string? pluginId) => TryGet(pluginId, out _);

    /// <summary>Called by a registration's Dispose: forget it and let the workspace drop the page.</summary>
    internal static void Forget(ModSettingsPageRegistration registration)
    {
        if (registration == null) return;
        if (Pages.TryGetValue(registration.PluginId, out var current) && ReferenceEquals(current, registration))
            Pages.Remove(registration.PluginId);
        Epoch++;
        ModsMenu.NotifySettingsPageRemoved(registration.PluginId);
    }

    /// <summary>The unload sweep: dispose every page <paramref name="owner"/> registered. Returns how many.</summary>
    internal static int RemoveAll(object owner)
    {
        var gone = new List<ModSettingsPageRegistration>();
        foreach (var page in Pages.Values) if (ReferenceEquals(page.Owner, owner)) gone.Add(page);
        foreach (var page in gone) page.Dispose();
        return gone.Count;
    }

    /// <summary>Core uninstall: every page goes.</summary>
    internal static void Uninstall()
    {
        foreach (var page in new List<ModSettingsPageRegistration>(Pages.Values)) page.Dispose();
        Pages.Clear();
    }

    internal static void ResetForTests() => Uninstall();
}

/// <summary>
/// LISTING1: section headings' help by title (<see cref="SettingsPageBuilder.SectionHelp"/>); the description column
/// shows it while a heading is hovered. Titles are unique per page, and every home splits a page by title.
/// </summary>
internal static class ModSettingsSectionHelp
{
    private static readonly Dictionary<string, string> ByTitle = new(StringComparer.Ordinal);

    internal static void Set(string title, string? help)
    {
        lock (ByTitle)
        {
            if (string.IsNullOrWhiteSpace(help)) ByTitle.Remove(title ?? "");
            else ByTitle[title ?? ""] = help!.Trim();
        }
    }

    internal static string For(string? title)
    {
        lock (ByTitle) return ByTitle.TryGetValue(title ?? "", out var h) ? h : "";
    }

    internal static void ResetForTests()
    {
        lock (ByTitle) ByTitle.Clear();
    }
}
