using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;

namespace Progenitor.Ui;

/// <summary>
/// Settings-kit row alignment. Center is the default so overview, consent and deny
/// pages do not move; Configuration and the Install listing opt into left.
/// </summary>
internal static class SettingsAlign
{
    public const string Left = "left";
    public const string Centre = "centre";

    public const float HeadingFontSize = 24f;
    public const float HeadingExtraTop = 16f;
    public const float NoteFontSize = 16f;
    public const float InlineButtonWidth = 72f;
    public const float InlineGap = 8f;
    public const float InlineFieldMinWidth = 80f;

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Left, StringComparison.OrdinalIgnoreCase)) return Left;
        return Centre;
    }

    public static bool IsLeft(string? value) =>
        string.Equals(Normalize(value), Left, StringComparison.Ordinal);

    /// <summary>
    /// Field + trailing action buttons on one row. <paramref name="x"/> values are
    /// centers in the row's local space (pivot at the row center), matching
    /// <c>PlaceTop</c>. Captions are not placed here: they fill each button.
    /// </summary>
    public static InlineRowGeometry InlineRow(float rowWidth, int buttonCount)
    {
        int n = buttonCount < 0 ? 0 : buttonCount;
        float gap = InlineGap;
        float btnW = InlineButtonWidth;
        float buttonsW = n * btnW + Math.Max(0, n - 1) * gap;
        float fieldW = rowWidth - buttonsW - (n > 0 ? gap : 0f);
        if (fieldW < InlineFieldMinWidth) fieldW = InlineFieldMinWidth;
        float left = -rowWidth * 0.5f;
        return new InlineRowGeometry(
            left + fieldW * 0.5f,
            fieldW,
            n > 0 ? left + fieldW + gap + btnW * 0.5f : 0f,
            btnW,
            gap,
            n);
    }
}

/// <summary>
/// Centers and widths for <see cref="SettingsAlign.InlineRow"/>.
/// </summary>
internal readonly struct InlineRowGeometry
{
    public InlineRowGeometry(
        float fieldX, float fieldWidth, float firstButtonX, float buttonWidth, float gap, int buttonCount)
    {
        FieldX = fieldX;
        FieldWidth = fieldWidth;
        FirstButtonX = firstButtonX;
        ButtonWidth = buttonWidth;
        Gap = gap;
        ButtonCount = buttonCount;
    }

    public float FieldX { get; }
    public float FieldWidth { get; }
    public float FirstButtonX { get; }
    public float ButtonWidth { get; }
    public float Gap { get; }
    public int ButtonCount { get; }

    public float ButtonX(int index) => FirstButtonX + index * (ButtonWidth + Gap);

    public float ButtonLeft(int index) => ButtonX(index) - ButtonWidth * 0.5f;

    public float ButtonRight(int index) => ButtonX(index) + ButtonWidth * 0.5f;

    public float FieldLeft => FieldX - FieldWidth * 0.5f;

    public float FieldRight => FieldX + FieldWidth * 0.5f;
}

/// <summary>
/// TMP properties the kit writes for a caption. ALIGN1 assigned
/// <c>TextAlignmentOptions.Left</c> / <c>Center</c> (513 / 514) to
/// <c>alignment</c> on every layout pass; those combined values include
/// vertical Middle, and through IL2CPP they can land unmasked in
/// <c>m_lineJustification</c>, which flush-justifies the line (gen 17
/// letter-spacing on every workspace label). The write is the horizontal
/// anchor only (Left=1, Center=2). Character and word spacing are never
/// written — a cloned donor keeps its tracking, a built label keeps TMP's
/// default (the gen 16 look).
/// </summary>
internal static class LabelTmpWrite
{
    /// <summary>The game's <c>TextAlignmentOptions.Left</c> (Middle | Left).</summary>
    public const int CombinedLeft = 513;

    /// <summary>The game's <c>TextAlignmentOptions.Center</c> (Middle | Center).</summary>
    public const int CombinedCenter = 514;

    public const int HorizontalMask = 0xFF;
    public const int HorizontalLeft = CombinedLeft & HorizontalMask;
    public const int HorizontalCenter = CombinedCenter & HorizontalMask;
    public const int Justified = 8;
    public const int Flush = 16;

    public static int Combined(bool left) => left ? CombinedLeft : CombinedCenter;

    public static int Horizontal(bool left) => Combined(left) & HorizontalMask;

    public static bool WritesCharacterSpacing => false;

    public static bool WritesWordSpacing => false;
}

/// <summary>
/// One editable line in <see cref="SettingsPanel"/>: either a registered keybind or one config
/// entry. Reading and writing both go through the row so the panel never touches
/// <c>ConfigEntry</c> directly and every write lands in the cfg file the same way.
/// </summary>
public sealed class SettingsRow
{
    private readonly ConfigEntryBase? _entry;
    private readonly IKeybindInfo? _keybind;
    private readonly SettingPresentation? _presentation;
    private string? _bounds;
    private string? _defaultValue;
    private string _alignment = SettingsAlign.Centre;

    private SettingsRow(
        string pluginId,
        string section,
        string key,
        string description,
        SettingsEditorKind kind,
        Type? valueType,
        ConfigEntryBase? entry,
        IKeybindInfo? keybind,
        bool synced,
        bool foreign = false,
        SettingPresentation? presentation = null)
    {
        IsForeign = foreign;
        PluginId = pluginId;
        Section = section;
        Key = key;
        Description = description ?? "";
        Kind = kind;
        ValueType = valueType;
        _entry = entry;
        _keybind = keybind;
        IsSynced = synced;
        _presentation = presentation;
    }

    /// <summary>Owning plugin GUID.</summary>
    public string PluginId { get; }

    /// <summary>Cfg section (<see cref="Keybinds.Section"/> for a keybind).</summary>
    public string Section { get; }

    /// <summary>Cfg key / keybind id.</summary>
    public string Key { get; }

    /// <summary>Cfg description, trimmed to one line for the panel.</summary>
    public string Description { get; }

    /// <summary>Which editor widget this row wants.</summary>
    public SettingsEditorKind Kind { get; }

    /// <summary>Which of the eight controls the UI should build. Shared presentation is not a widget.</summary>
    public SettingsEditorHint EditorHint =>
        _presentation?.EditorHint
        ?? (Kind == SettingsEditorKind.Keybind
            ? SettingsEditorHint.Keybind
            : Kind == SettingsEditorKind.Action
                ? SettingsEditorHint.Action
                : SettingsEditors.HintFor(ValueType));

    /// <summary>
    /// SETTINGS1 review finding 5: the entry lives in another plugin's config file although this
    /// plugin's page shows it. Such a row is never part of a page-wide reset.
    /// </summary>
    internal bool OwnedElsewhere { get; private set; }

    internal SettingsRow MarkOwnedElsewhere(bool elsewhere)
    {
        OwnedElsewhere = elsewhere;
        return this;
    }

    /// <summary>The registered or builder presentation, or null (a row merged with a server definition keeps its words).</summary>
    internal SettingPresentation? Presentation => _presentation;

    /// <summary>True when a registered presentation names the control explicitly, rather than the type deciding it.</summary>
    internal bool HasPresentationHint => _presentation?.EditorHint != null;

    /// <summary>OPTIONHOVER1: the author's explanation of each choice (<see cref="SettingPresentation.ChoiceDescriptions"/>), or null.</summary>
    internal string[]? ChoiceDescriptions => _presentation?.ChoiceDescriptions;

    /// <summary>SETTINGS1 stage 2: a registered or builder presentation names the row (<see cref="SettingPresentation.Label"/>).</summary>
    internal bool HasPresentationLabel => !string.IsNullOrEmpty(_presentation?.Label);

    /// <summary>A keybind row's registered default chord; null for every other row.</summary>
    internal KeyChord? DefaultChord => _keybind?.DefaultChord;

    /// <summary>When a widget write becomes the stored value. See <see cref="SettingCommitPolicy"/>.</summary>
    public SettingCommitPolicy CommitPolicy =>
        _presentation?.CommitPolicy ?? SettingsEditors.CommitPolicyFor(EditorHint);

    /// <summary>Local file vs server-owned ConfigSync value.</summary>
    public SettingScope Scope => IsSyncedNow ? SettingScope.Server : SettingScope.Local;

    /// <summary>How a write is allowed to reach the stored value.</summary>
    public SettingEditPath EditPath
    {
        get
        {
            if (Kind == SettingsEditorKind.Action)
                return SettingEditPath.LocalConfig;
            if (IsForeign) return SettingEditPath.None;
            if (IsSyncedNow)
                return Progenitor.Networking.ServerSettings.CanRequest(RowId)
                    ? SettingEditPath.ServerRequest
                    : SettingEditPath.None;
            if (Kind == SettingsEditorKind.ReadOnly) return SettingEditPath.None;
            return SettingEditPath.LocalConfig;
        }
    }

    /// <summary>CLR type behind the value, or null for a keybind.</summary>
    public Type? ValueType { get; }

    /// <summary>
    /// True for a config-sync entry the server owns: shown, never editable. Captured when the
    /// panel was built; <see cref="IsSyncedNow"/> is the one a write consults.
    /// </summary>
    public bool IsSynced { get; }

    /// <summary>
    /// The same question asked again, now. A panel opened at the main menu keeps its rows for the
    /// whole time it is shown, so an entry the server took over on join would still have an
    /// editable text box behind it — and a write there would put the server's value into the
    /// player's own <c>GUID.cfg</c>, which is the one thing ui-input.md §11.5 forbids (R34 panel
    /// note 3). Cheap enough to ask per write; never asked per frame.
    /// </summary>
    public bool IsSyncedNow => IsSynced || (_entry != null && SettingsCatalog.IsSynced(_entry));

    /// <summary>
    /// The entry's <c>AcceptableValueRange</c> / <c>AcceptableValueList</c> as one line, or
    /// <c>""</c>. Shown in the row label so the allowed set is visible before the player types,
    /// and repeated in the rejection message (R34 finding 8).
    /// </summary>
    public string Bounds => _bounds ??= SettingsBounds.Describe(_entry);

    /// <summary>
    /// True for another BepInEx mod's setting, shown by the chainloader sweep and not editable
    /// here (<see cref="SettingsCatalog.AllowForeignEdits"/>).
    /// </summary>
    public bool IsForeign { get; }

    /// <summary>
    /// Panel caption. A registered presentation label wins; otherwise
    /// <c>[Section] Key</c>, namespaced by plugin in the header.
    /// </summary>
    public string Label =>
        !string.IsNullOrEmpty(_presentation?.Label) ? _presentation!.Label : "[" + Section + "] " + Key;

    /// <summary>Stable identity for the panel's widget map.</summary>
    public string RowId => PluginId + "/" + Section + "/" + Key;

    /// <summary>
    /// <see cref="SettingsAlign.Left"/> or <see cref="SettingsAlign.Centre"/>. Center is the
    /// kit default; a settings form opts in per row.
    /// </summary>
    public string Alignment => _alignment;

    internal SettingsRow WithAlignment(string alignment)
    {
        _alignment = SettingsAlign.Normalize(alignment);
        return this;
    }

    internal static SettingsRow ForKeybind(IKeybindInfo bind) =>
        new(bind.PluginId,
            Keybinds.Section,
            bind.Id,
            bind.Description,
            SettingsEditorKind.Keybind,
            valueType: null,
            entry: null,
            keybind: bind,
            synced: false);

    internal static SettingsRow ForEntry(
        string pluginId,
        ConfigEntryBase entry,
        bool synced,
        bool foreign = false,
        SettingPresentation? presentation = null)
    {
        var kind = synced || foreign
            ? SettingsEditorKind.ReadOnly
            : SettingsEditors.KindFor(entry.SettingType);
        return new SettingsRow(
            pluginId,
            entry.Definition.Section,
            entry.Definition.Key,
            presentation?.Help ?? DescribeEntry(entry),
            kind,
            entry.SettingType,
            entry,
            keybind: null,
            synced,
            foreign,
            presentation);
    }

    internal static SettingsRow ForAction(SettingActionBinding action)
    {
        var described = action.Describe();
        return new SettingsRow(
            action.PluginId,
            SettingBinding.ActionSection,
            action.Id,
            described.Help,
            SettingsEditorKind.Action,
            valueType: null,
            entry: null,
            keybind: null,
            synced: false,
            foreign: false,
            presentation: new SettingPresentation
            {
                EditorHint = SettingsEditorHint.Action,
                CommitPolicy = SettingCommitPolicy.Immediate,
                Label = described.Label,
                Help = described.Help,
                Group = described.Group,
                Order = described.Order,
            });
    }

    /// <summary>Serializable snapshot. Delegates stay on the live binding, not here.</summary>
    public SettingDescriptor ToDescriptor()
    {
        string stored = Read();
        string[] allowed = _presentation?.Choices ?? AllowedValues();
        string[] labels = _presentation?.ChoiceLabels ?? allowed;
        string? min = _presentation?.Min;
        string? max = _presentation?.Max;
        if (min == null && max == null)
        {
            string bounds = Bounds;
            int to = bounds.IndexOf(" to ", StringComparison.Ordinal);
            if (to > 0)
            {
                min = bounds.Substring(0, to).Trim();
                max = bounds.Substring(to + 4).Trim();
            }
        }

        return new SettingDescriptor(
            PluginId,
            Section,
            Key,
            ValueType?.FullName ?? (Kind == SettingsEditorKind.Action ? "Action" : Kind == SettingsEditorKind.Keybind ? "Keybind" : ""),
            stored,
            DefaultValue,
            Description,
            _presentation?.Group ?? Section,
            _presentation?.Order ?? 0,
            EditorHint,
            CommitPolicy,
            Scope,
            EditPath,
            min,
            max,
            _presentation?.Step,
            _presentation?.MaxLength,
            allowed,
            labels,
            _presentation?.Units,
            _presentation?.HasAlpha ?? false,
            EditPath != SettingEditPath.None,
            _presentation?.Label);
    }

    /// <summary>Invariant-formatted default Reset would write.</summary>
    public string DefaultValue
    {
        get
        {
            if (_defaultValue != null) return _defaultValue;
            if (_keybind != null) return _defaultValue = _keybind.DefaultChord.ToString();
            if (_presentation != null && !string.IsNullOrEmpty(_presentation.DefaultValue))
                return _defaultValue = _presentation.DefaultValue!;
            if (_entry != null) return _defaultValue = SettingBinding.ResolveDefault(_entry, _presentation ?? new SettingPresentation());
            return _defaultValue = Read();
        }
    }

    private string[] AllowedValues()
    {
        if (ValueType != null && ValueType.IsEnum) return Enum.GetNames(ValueType);
        return SettingsBounds.Options(_entry, Bounds);
    }

    /// <summary>The text the editor shows right now.</summary>
    public string Read()
    {
        if (_keybind != null) return _keybind.Chord.ToString();
        if (_entry == null) return "";
        try { return SettingsEditors.Format(_entry.BoxedValue); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings read of " + RowId + " threw: " + e.Message);
            return "";
        }
    }

    /// <summary>
    /// Write the player's text back through the config entry, so BepInEx's own
    /// <c>SettingChanged</c> + auto-save updates the cfg file on disk. False with an
    /// <paramref name="error"/> when the text does not parse, when the row is read-only, or when
    /// the write threw — nothing is written in any of those cases.
    /// </summary>
    public bool TryWrite(string? text, out string error)
    {
        error = "";
        if (_keybind != null)
        {
            if (!KeyChord.TryParseTyped(text, out var chord, out error)) return false;

            // KEYS2: a reserved or already-used chord is refused with its reason.
            return Keybinds.TryRebind(PluginId, Key, chord, Keybinds.RebindCheck.Full, out var why)
                || Fail(why ?? "this keybind is no longer registered", out error);
        }

        // Asked live, not from the flag captured at panel-open: the server may have taken this
        // entry over while the panel was sitting open (R34 panel note 3).
        if (IsSyncedNow)
        {
            error = "the server owns this value for this session";
            return false;
        }

        if (IsForeign)
        {
            error = "this setting belongs to another mod; Progenitor cannot tell whether its "
                + "server syncs it, so it is shown but not edited here";
            return false;
        }

        if (_entry == null || Kind == SettingsEditorKind.ReadOnly)
        {
            error = (ValueType?.Name ?? "this value") + " is not editable here";
            return false;
        }

        if (!SettingsEditors.TryParse(text, ValueType, out var parsed, out error))
            return false;

        // TEXTAREA1: the same author cap SettingBinding.TryValidate applies, so an entry that was
        // never registered (or a bridge write) cannot store a longer value than the row allows.
        if (_presentation?.MaxLength is int max && max >= 0 && (text ?? "").Length > max)
        {
            error = "value is longer than " + max + " characters";
            return false;
        }

        // BepInEx clamps an out-of-range set instead of throwing, and AcceptableValueList clamps to
        // its FIRST element — so writing here would quietly store something the player did not
        // type and the panel would log it as a success (R34 finding 8). Ask first, refuse loudly.
        if (SettingsBounds.Check(_entry, parsed) == SettingsBounds.Verdict.Invalid)
        {
            string allowed = Bounds;
            error = "outside the values this setting accepts"
                + (allowed.Length > 0 ? " (" + allowed + ")" : "");
            return false;
        }

        try
        {
            // An unchanged commit writes nothing. onEndEdit fires on every focus loss, and
            // BepInEx's SaveOnConfigSet rewrites the whole cfg file on every set, so tabbing
            // through a panel used to be one full disk write per row (R34 finding 1).
            object? current;
            try { current = _entry.BoxedValue; }
            catch { current = null; }

            bool changed = !Equals(current, parsed);
            if (changed) _entry.BoxedValue = parsed;

            // SAVEHOLD1: on a cfg whose auto-save a synced lock holds off, the write reaches the disk
            // only through an explicit save; record how it was saved for the readback.
            try { SettingPersistence.AfterWrite(RowId, _entry, changed); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings save of " + RowId + " threw: " + e.Message); }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Plugin.Logger?.LogWarning("Ui: settings write of " + RowId + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>SAVEHOLD1: the entry this row writes (the persistence readback compares instances).</summary>
    internal ConfigEntryBase? EntryForReadback => _entry;

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string DescribeEntry(ConfigEntryBase entry)
    {
        // ConfigDescription is not on the test shim, and a live description can be several lines.
        // Take the first line reflectively when it is there; the row is still useful without it.
        try
        {
            var prop = entry.GetType().GetProperty("Description");
            var description = prop?.GetValue(entry);
            var textProp = description?.GetType().GetProperty("Description");
            if (textProp?.GetValue(description) is string text && text.Length > 0)
            {
                int nl = text.IndexOf('\n');
                return (nl < 0 ? text : text.Substring(0, nl)).Trim();
            }
        }
        catch
        {
            // Description is decoration; never fail a row over it.
        }

        return "";
    }
}

/// <summary>
/// The <c>AcceptableValueRange</c> / <c>AcceptableValueList</c> half of a config entry, read
/// reflectively.
///
/// <para>
/// <b>Why this exists (R34 finding 8).</b> BepInEx does not throw when a value outside the
/// acceptable set is assigned to <c>BoxedValue</c> — it <i>clamps</i>, and
/// <c>AcceptableValueList.Clamp</c> returns <c>AcceptableValues[0]</c> for anything invalid, not
/// the nearest value and not the previous one. So a panel that wrote and reported success turned
/// "the player typed <c>high</c>" into "<c>Difficulty</c> is now <c>Low</c>" and logged it as an
/// accepted edit. The panel therefore asks first: an entry whose bounds refuse the parsed value is
/// rejected with the allowed set in the message, and nothing is written.
/// </para>
///
/// <para>
/// <b>Why reflection.</b> <c>ConfigDescription</c> and the <c>AcceptableValue*</c> types are not on
/// the test shim (the test project compiles these files against its own minimal
/// <c>BepInEx.Configuration</c>), and this file may not add them. Everything is duck-typed off the
/// live objects — <c>Description.AcceptableValues</c>, then <c>IsValid(object)</c> and
/// <c>ToDescriptionString()</c> — with the member handles cached per type. Any failure answers
/// <see cref="Verdict.NoBounds"/>: an entry we cannot classify is written exactly as before, which
/// is the behavior this file replaces rather than a new risk.
/// </para>
/// </summary>
internal static class SettingsBounds
{
    /// <summary>What the bounds say about one candidate value.</summary>
    internal enum Verdict
    {
        /// <summary>The entry has no acceptable-value constraint, or it could not be read.</summary>
        NoBounds = 0,

        /// <summary>The bounds accept this value; the write will store it as typed.</summary>
        Valid = 1,

        /// <summary>The bounds refuse it. Writing would silently clamp, so the panel refuses instead.</summary>
        Invalid = 2,
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Members> Cache = new();

    /// <summary>
    /// One line for the row label and the rejection message — <c>"one of: Low, Med, High"</c> or
    /// <c>"1 to 10"</c> — or <c>""</c> when the entry has no bounds. Never throws.
    /// </summary>
    internal static string Describe(ConfigEntryBase? entry)
    {
        var values = AcceptableValues(entry);
        if (values == null) return "";
        var members = MembersFor(values.GetType());

        try
        {
            if (members.ToDescriptionString?.Invoke(values, Array.Empty<object>()) is string text && text.Length > 0)
                return Tidy(text);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings acceptable-values description threw: " + e.Message);
        }

        // Fall back to the two shapes BepInEx ships, in case a build renames the description
        // helper: a range with MinValue/MaxValue, or a list with an AcceptableValues array.
        try
        {
            if (members.Min != null && members.Max != null)
            {
                return Text(members.Min.GetValue(values)) + " to " + Text(members.Max.GetValue(values));
            }

            if (members.List?.GetValue(values) is IEnumerable list)
            {
                var parts = new List<string>();
                foreach (var item in list) parts.Add(Text(item));
                if (parts.Count > 0) return "one of: " + string.Join(", ", parts);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings acceptable-values read threw: " + e.Message);
        }

        return "";
    }

    /// <summary>
    /// The options of an <c>AcceptableValueList</c>, read from its <c>AcceptableValues</c> array so an
    /// option containing a comma (<c>"Always, even in combat"</c>) stays one option. BepInEx's
    /// description joins the values with <c>", "</c> and does not escape them, so the text cannot be
    /// split back reliably; <paramref name="bounds"/> (that description, tidied) is split on commas
    /// only for a bounds type with no readable array. Empty when the entry has no finite list.
    /// </summary>
    internal static string[] Options(ConfigEntryBase? entry, string bounds)
    {
        var values = AcceptableValues(entry);
        if (values != null)
        {
            try
            {
                if (MembersFor(values.GetType()).List?.GetValue(values) is IEnumerable list)
                {
                    var options = new List<string>();
                    foreach (var item in list) options.Add(SettingsEditors.Format(item));
                    if (options.Count > 0) return options.ToArray();
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: settings acceptable-values read threw: " + e.Message);
            }
        }

        if (!bounds.StartsWith("one of: ", StringComparison.Ordinal)) return Array.Empty<string>();
        var parts = bounds.Substring("one of: ".Length).Split(',');
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        return parts;
    }

    /// <summary>
    /// Whether <paramref name="value"/> may be written to <paramref name="entry"/>. An entry with
    /// no bounds, or bounds we cannot read, answers <see cref="Verdict.NoBounds"/> — never
    /// <see cref="Verdict.Invalid"/>, because refusing an edit on a failed reflection call would
    /// make an unrelated BepInEx change look like a broken panel.
    /// </summary>
    internal static Verdict Check(ConfigEntryBase? entry, object? value)
    {
        var values = AcceptableValues(entry);
        if (values == null) return Verdict.NoBounds;

        var members = MembersFor(values.GetType());
        if (members.IsValid == null) return Verdict.NoBounds;

        try
        {
            if (members.IsValid.Invoke(values, new[] { value }) is bool ok)
                return ok ? Verdict.Valid : Verdict.Invalid;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings acceptable-values check threw: " + e.Message);
        }

        return Verdict.NoBounds;
    }

    internal static void ResetCache()
    {
        lock (Gate) Cache.Clear();
    }

    private static object? AcceptableValues(ConfigEntryBase? entry)
    {
        if (entry == null) return null;
        try
        {
            var description = entry.GetType().GetProperty("Description")?.GetValue(entry);
            if (description == null) return null;
            return description.GetType().GetProperty("AcceptableValues")?.GetValue(description);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings acceptable-values lookup threw: " + e.Message);
            return null;
        }
    }

    private static Members MembersFor(Type type)
    {
        // A collectible type (a plugin's own AcceptableValueBase, or AcceptableValueList<T> over a
        // plugin enum) is never cached: a Core static keyed by it would root the plugin's ALC
        // after unload. Its members are cheap to look up again.
        if (type.IsCollectible) return new Members(type);
        lock (Gate)
        {
            if (Cache.TryGetValue(type, out var cached)) return cached;
            var members = new Members(type);
            Cache[type] = members;
            return members;
        }
    }

    /// <summary>Test seam: resolve <paramref name="type"/>'s members the way a bounds check does.</summary>
    internal static void ResolveMembersForTests(Type type) => MembersFor(type);

    /// <summary>How many bounds types are cached.</summary>
    internal static int CachedTypeCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    private static string Text(object? value) =>
        value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";

    /// <summary>
    /// BepInEx writes its description as a cfg comment — <c>"# Acceptable values: A, B"</c> or
    /// <c>"# Acceptable value range: From 1 to 10"</c>. The panel is not a cfg file.
    /// </summary>
    private static string Tidy(string text)
    {
        string s = text.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1).Trim();
        if (s.StartsWith("Acceptable value range:", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("Acceptable value range:".Length).Trim();
        else if (s.StartsWith("Acceptable values:", StringComparison.OrdinalIgnoreCase))
            s = "one of: " + s.Substring("Acceptable values:".Length).Trim();
        if (s.StartsWith("From ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5).Trim();
        return s;
    }

    private readonly struct Members
    {
        public Members(Type type)
        {
            MethodInfo? isValid = null;
            MethodInfo? describe = null;
            PropertyInfo? min = null;
            PropertyInfo? max = null;
            PropertyInfo? list = null;
            try
            {
                isValid = type.GetMethod("IsValid", new[] { typeof(object) });
                describe = type.GetMethod("ToDescriptionString", Type.EmptyTypes);
                min = type.GetProperty("MinValue");
                max = type.GetProperty("MaxValue");
                list = type.GetProperty("AcceptableValues");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: settings acceptable-values reflection threw: " + e.Message);
            }

            IsValid = isValid;
            ToDescriptionString = describe;
            Min = min;
            Max = max;
            List = list;
        }

        public MethodInfo? IsValid { get; }

        public MethodInfo? ToDescriptionString { get; }

        public PropertyInfo? Min { get; }

        public PropertyInfo? Max { get; }

        public PropertyInfo? List { get; }
    }
}
