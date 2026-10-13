using System;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// Which of the eight settings widgets a row wants. Read-only value, heading/help, validation
/// message, Reset, Apply and Cancel are shared presentation, not extra widget types.
/// </summary>
public enum SettingsEditorHint
{
    /// <summary>Boolean option: label, help and visible current value.</summary>
    Toggle = 1,

    /// <summary>Integer or decimal field with stepper, limits, step and units.</summary>
    Numeric = 2,

    /// <summary>
    /// Finite bounded value with a slider and a numeric readout. Drag updates a local preview
    /// and commits once on release — never a network write per frame.
    /// </summary>
    Slider = 3,

    /// <summary>Enum or finite choice list: readable labels, stable underlying values.</summary>
    Dropdown = 4,

    /// <summary>Free text with validation and optional length limit.</summary>
    Text = 5,

    /// <summary>Key capture, cancel, clear/reset, readable chord and conflict notice.</summary>
    Keybind = 6,

    /// <summary>Swatch plus editable components/hex and optional alpha.</summary>
    Color = 7,

    /// <summary>Invoke a registered function; show busy/result/error; one pending invocation.</summary>
    Action = 8,
}

/// <summary>
/// When a widget write becomes the stored value. Stated here so UI, commands and a future
/// player MCP share one policy.
/// </summary>
public enum SettingCommitPolicy
{
    /// <summary>
    /// Commit on the discrete change itself. Default for <see cref="SettingsEditorHint.Toggle"/>
    /// and <see cref="SettingsEditorHint.Dropdown"/> where existing behavior already writes
    /// through the config entry on the click.
    /// </summary>
    Immediate = 0,

    /// <summary>
    /// Commit on Apply or another explicit submission (Enter / end-edit). Default for
    /// <see cref="SettingsEditorHint.Text"/>, <see cref="SettingsEditorHint.Numeric"/>,
    /// <see cref="SettingsEditorHint.Color"/> and <see cref="SettingsEditorHint.Keybind"/>.
    /// </summary>
    OnSubmit = 1,

    /// <summary>
    /// Slider drag updates a local preview only; one commit on release or explicit confirmation.
    /// Never a network write per frame. Default for <see cref="SettingsEditorHint.Slider"/>.
    /// </summary>
    OnRelease = 2,
}

/// <summary>
/// Who a setting affects (docs/settings-scopes.md, decided 2026-10-03). <see cref="Personal"/>
/// and <see cref="ServerWide"/> are the names to use; <see cref="Local"/> and <see cref="Server"/>
/// are the earlier names of the same two values and stay so compiled plugins keep working. Where
/// the value is stored follows from the scope and which half of the mod owns it.
/// </summary>
public enum SettingScope
{
    /// <summary>Earlier name of <see cref="Personal"/>: this process's own config file.</summary>
    Local = 0,

    /// <summary>Earlier name of <see cref="ServerWide"/>: server-authoritative via ConfigSync; the local file is never overwritten.</summary>
    Server = 1,

    /// <summary>The player changes it for themselves (a client-owned value lives in their own cfg).</summary>
    Personal = Local,

    /// <summary>It affects everyone on the server: only admins change it, everyone may read it.</summary>
    ServerWide = Server,
}

/// <summary>How a write is allowed to reach the stored value.</summary>
public enum SettingEditPath
{
    /// <summary>Read-only: foreign, unsupported, or a server value with no admin request.</summary>
    None = 0,

    /// <summary>Write the wrapped <c>ConfigEntry</c>; BepInEx persists the local cfg.</summary>
    LocalConfig = 1,

    /// <summary>
    /// Authenticated, server-validated request. The widget must not set admin flags or write
    /// the server value into a local file.
    /// </summary>
    ServerRequest = 2,
}

/// <summary>Lifecycle of one submit against a setting.</summary>
public enum SettingWriteState
{
    Idle = 0,
    Preview = 1,
    Pending = 2,
    Accepted = 3,
    Refused = 4,
    Failed = 5,
}

/// <summary>Explicit outcome of a registered settings action.</summary>
public enum SettingActionOutcome
{
    Completed = 0,
    Refused = 1,
    Failed = 2,
    Busy = 3,
    Unavailable = 4,
    Stale = 5,
}

/// <summary>
/// Serializable description of one setting. No delegates, no context types — a future player
/// MCP can consume this snapshot. Current and default values are invariant-formatted text.
/// </summary>
public sealed class SettingDescriptor
{
    public SettingDescriptor(
        string pluginId,
        string section,
        string key,
        string typeName,
        string currentValue,
        string defaultValue,
        string help,
        string group,
        int order,
        SettingsEditorHint editorHint,
        SettingCommitPolicy commitPolicy,
        SettingScope scope,
        SettingEditPath editPath,
        string? min = null,
        string? max = null,
        string? step = null,
        int? maxLength = null,
        string[]? allowedValues = null,
        string[]? choiceLabels = null,
        string? units = null,
        bool hasAlpha = false,
        bool canEdit = false,
        string? label = null)
    {
        PluginId = pluginId ?? "";
        Section = section ?? "";
        Key = key ?? "";
        TypeName = typeName ?? "";
        CurrentValue = currentValue ?? "";
        DefaultValue = defaultValue ?? "";
        Help = help ?? "";
        Group = group ?? "";
        Order = order;
        EditorHint = editorHint;
        CommitPolicy = commitPolicy;
        Scope = scope;
        EditPath = editPath;
        Min = min;
        Max = max;
        Step = step;
        MaxLength = maxLength;
        AllowedValues = allowedValues ?? Array.Empty<string>();
        ChoiceLabels = choiceLabels ?? Array.Empty<string>();
        Units = units ?? "";
        HasAlpha = hasAlpha;
        CanEdit = canEdit;
        Label = string.IsNullOrEmpty(label) ? Key : label;
    }

    /// <summary>Owning plugin GUID.</summary>
    public string PluginId { get; }

    /// <summary>Cfg section, or <c>Actions</c> for a registered action.</summary>
    public string Section { get; }

    /// <summary>Cfg key / action id.</summary>
    public string Key { get; }

    /// <summary>Stable identity: <c>pluginId/section/key</c>.</summary>
    public string Id => PluginId + "/" + Section + "/" + Key;

    /// <summary>CLR type name of the stored value, or <c>Action</c>.</summary>
    public string TypeName { get; }

    /// <summary>Invariant-formatted value currently stored (not an uncommitted preview).</summary>
    public string CurrentValue { get; }

    /// <summary>Invariant-formatted default used by Reset.</summary>
    public string DefaultValue { get; }

    public string Help { get; }

    public string Group { get; }

    public int Order { get; }

    public string Label { get; }

    public SettingsEditorHint EditorHint { get; }

    public SettingCommitPolicy CommitPolicy { get; }

    public SettingScope Scope { get; }

    public SettingEditPath EditPath { get; }

    public string? Min { get; }

    public string? Max { get; }

    public string? Step { get; }

    public int? MaxLength { get; }

    public string[] AllowedValues { get; }

    public string[] ChoiceLabels { get; }

    public string Units { get; }

    public bool HasAlpha { get; }

    public bool CanEdit { get; }

    /// <summary>JSON object with only primitives and string arrays. No delegates.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        Append(sb, "pluginId", PluginId, first: true);
        Append(sb, "section", Section);
        Append(sb, "key", Key);
        Append(sb, "id", Id);
        Append(sb, "typeName", TypeName);
        Append(sb, "currentValue", CurrentValue);
        Append(sb, "defaultValue", DefaultValue);
        Append(sb, "help", Help);
        Append(sb, "group", Group);
        Append(sb, "label", Label);
        Append(sb, "order", Order);
        Append(sb, "editorHint", EditorHint.ToString());
        Append(sb, "commitPolicy", CommitPolicy.ToString());
        Append(sb, "scope", SettingScopes.LegacyName(Scope));
        Append(sb, "editPath", EditPath.ToString());
        Append(sb, "min", Min);
        Append(sb, "max", Max);
        Append(sb, "step", Step);
        if (MaxLength.HasValue) Append(sb, "maxLength", MaxLength.Value);
        Append(sb, "units", Units);
        Append(sb, "hasAlpha", HasAlpha);
        Append(sb, "canEdit", CanEdit);
        Append(sb, "allowedValues", AllowedValues);
        Append(sb, "choiceLabels", ChoiceLabels);
        sb.Append('}');
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, string? value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(name).Append("\":");
        if (value == null) { sb.Append("null"); return; }
        sb.Append('"').Append(Escape(value)).Append('"');
    }

    private static void Append(StringBuilder sb, string name, int value)
    {
        sb.Append(",\"").Append(name).Append("\":").Append(value);
    }

    private static void Append(StringBuilder sb, string name, bool value)
    {
        sb.Append(",\"").Append(name).Append("\":").Append(value ? "true" : "false");
    }

    private static void Append(StringBuilder sb, string name, string[] values)
    {
        sb.Append(",\"").Append(name).Append("\":[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Escape(values[i])).Append('"');
        }

        sb.Append(']');
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }
}

/// <summary>Serializable description of one registered action. No delegates.</summary>
public sealed class SettingActionDescriptor
{
    public SettingActionDescriptor(
        string pluginId,
        string id,
        string label,
        string help,
        string group,
        int order,
        string owner,
        bool available,
        bool busy,
        string[] argumentNames,
        string[] argumentTypes,
        bool[] argumentRequired,
        string[] argumentDefaults)
    {
        PluginId = pluginId ?? "";
        Id = id ?? "";
        Label = label ?? Id;
        Help = help ?? "";
        Group = group ?? "";
        Order = order;
        Owner = owner ?? "";
        Available = available;
        Busy = busy;
        ArgumentNames = argumentNames ?? Array.Empty<string>();
        ArgumentTypes = argumentTypes ?? Array.Empty<string>();
        ArgumentRequired = argumentRequired ?? Array.Empty<bool>();
        ArgumentDefaults = argumentDefaults ?? Array.Empty<string>();
    }

    public string PluginId { get; }

    public string Id { get; }

    /// <summary>Stable identity: <c>pluginId/Actions/id</c>.</summary>
    public string SettingId => PluginId + "/" + SettingBinding.ActionSection + "/" + Id;

    public string Label { get; }

    public string Help { get; }

    public string Group { get; }

    public int Order { get; }

    public string Owner { get; }

    public bool Available { get; }

    public bool Busy { get; }

    public string[] ArgumentNames { get; }

    public string[] ArgumentTypes { get; }

    public bool[] ArgumentRequired { get; }

    public string[] ArgumentDefaults { get; }
}

/// <summary>Outcome of one write through the shared handler.</summary>
public readonly struct SettingWriteResult
{
    public SettingWriteResult(SettingWriteState state, string value, string error)
    {
        State = state;
        Value = value ?? "";
        Error = error ?? "";
    }

    public SettingWriteState State { get; }

    /// <summary>Stored value after an accept, or the unchanged current value otherwise.</summary>
    public string Value { get; }

    public string Error { get; }

    /// <summary>True only when the stored value was accepted. Pending is not applied.</summary>
    public bool Applied => State == SettingWriteState.Accepted;

    public static SettingWriteResult Idle(string value) =>
        new(SettingWriteState.Idle, value, "");

    public static SettingWriteResult Preview(string value) =>
        new(SettingWriteState.Preview, value, "");

    public static SettingWriteResult Pending(string value) =>
        new(SettingWriteState.Pending, value, "");

    public static SettingWriteResult Accepted(string value) =>
        new(SettingWriteState.Accepted, value, "");

    public static SettingWriteResult Refused(string value, string error) =>
        new(SettingWriteState.Refused, value, error);

    public static SettingWriteResult Failed(string value, string error) =>
        new(SettingWriteState.Failed, value, error);
}

/// <summary>Outcome of <see cref="SettingsCatalog.InvokeAction"/>.</summary>
public readonly struct SettingActionResult
{
    public SettingActionResult(SettingActionOutcome outcome, string message)
    {
        Outcome = outcome;
        Message = message ?? "";
    }

    public SettingActionOutcome Outcome { get; }

    public string Message { get; }

    public bool Completed => Outcome == SettingActionOutcome.Completed;

    public static SettingActionResult Ok(string message = "") =>
        new(SettingActionOutcome.Completed, message);

    public static SettingActionResult Refuse(string message) =>
        new(SettingActionOutcome.Refused, message);

    public static SettingActionResult Fail(string message) =>
        new(SettingActionOutcome.Failed, message);

    public static SettingActionResult BusyAlready() =>
        new(SettingActionOutcome.Busy, "This action is already running.");

    public static SettingActionResult NotAvailable(string message) =>
        new(SettingActionOutcome.Unavailable, message);

    public static SettingActionResult StaleGeneration() =>
        new(SettingActionOutcome.Stale, "This action registration is no longer live.");
}

/// <summary>Author overrides for editor choice, labels, units, bounds and presentation.</summary>
public sealed class SettingPresentation
{
    public SettingsEditorHint? EditorHint { get; set; }

    public SettingCommitPolicy? CommitPolicy { get; set; }

    public string? Label { get; set; }

    public string? Help { get; set; }

    public string? Group { get; set; }

    public int Order { get; set; }

    public string? Units { get; set; }

    public string? Step { get; set; }

    public string? Min { get; set; }

    public string? Max { get; set; }

    public int? MaxLength { get; set; }

    public string[]? Choices { get; set; }

    public string[]? ChoiceLabels { get; set; }

    /// <summary>
    /// OPTIONHOVER1: a short explanation of each choice, index-aligned with the choices (<see cref="Choices"/>, else the
    /// enum's or <c>AcceptableValueList</c>'s values; an empty entry has none). A radio list draws it under the option; the
    /// settings page's details pane shows it while the pointer is over the option.
    /// </summary>
    public string[]? ChoiceDescriptions { get; set; }

    public string? DefaultValue { get; set; }

    /// <summary>
    /// Opt the wrapped synced entry into <c>ServerSettings</c> administrator requests.
    /// Same permission as <see cref="PluginMenu.AllowServerEdits"/>; the local widget still
    /// never writes the server value into a local file.
    /// </summary>
    public bool AllowServerEdits { get; set; }

    public bool HasAlpha { get; set; }
}

/// <summary>
/// Typed arguments, ownership and availability for an action. The handler is registered
/// separately so this object stays serializable.
/// </summary>
public sealed class SettingActionSpec
{
    public string? Label { get; set; }

    public string? Help { get; set; }

    public string? Group { get; set; }

    public int Order { get; set; }

    public string[]? ArgumentNames { get; set; }

    public string[]? ArgumentTypes { get; set; }

    public bool[]? ArgumentRequired { get; set; }

    public string[]? ArgumentDefaults { get; set; }

    public Func<bool>? IsAvailable { get; set; }
}
