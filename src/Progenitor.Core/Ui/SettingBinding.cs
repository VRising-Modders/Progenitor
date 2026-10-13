using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// Live registration that wraps an existing config entry as the source of truth. Delegates and
/// widgets live here, not on <see cref="SettingDescriptor"/>. Closing or disposing detaches
/// callbacks and rejects a previous generation's pending completion for the same key.
/// </summary>
public sealed class SettingBinding : IDisposable
{
    internal const string ActionSection = "Actions";

    private readonly object _gate = new();
    private readonly string _pluginId;
    private readonly string _section;
    private readonly string _key;
    private readonly object? _owner;
    private readonly ConfigEntryBase? _entry;
    private readonly IKeybindInfo? _keybind;
    private readonly SettingPresentation _presentation;
    private readonly string _defaultValue;
    private readonly Type? _valueType;
    private readonly bool _foreign;
    private Action<SettingDescriptor>? _valueChanged;
    private string? _draft;
    private string _lastRaised;
    private SettingWriteState _state;
    private string _lastError = "";
    private IDisposable? _serverRequest;
    private IDisposable? _serverRegistration;
    private bool _disposed;
    private bool _raising;

    internal SettingBinding(
        string pluginId,
        ConfigEntryBase? entry,
        IKeybindInfo? keybind,
        SettingPresentation? presentation,
        object? owner,
        int generation,
        bool foreign)
    {
        _pluginId = pluginId ?? "";
        _entry = entry;
        _keybind = keybind;
        _owner = owner;
        _presentation = presentation ?? new SettingPresentation();
        _foreign = foreign;
        Generation = generation;
        if (keybind != null)
        {
            _section = Keybinds.Section;
            _key = keybind.Id;
            _valueType = null;
            _defaultValue = keybind.Chord.ToString();
        }
        else if (entry != null)
        {
            _section = entry.Definition.Section;
            _key = entry.Definition.Key;
            _valueType = entry.SettingType;
            _defaultValue = ResolveDefault(entry, _presentation);
        }
        else
        {
            _section = "";
            _key = "";
            _valueType = null;
            _defaultValue = "";
        }

        _lastRaised = ReadStored();
        _state = SettingWriteState.Idle;
    }

    public int Generation { get; }

    public string Id => _pluginId + "/" + _section + "/" + _key;

    public string PluginId => _pluginId;

    public object? Owner => _owner;

    public SettingWriteState State
    {
        get { lock (_gate) return _disposed ? SettingWriteState.Failed : _state; }
    }

    public bool HasUncommittedEdit
    {
        get { lock (_gate) return !_disposed && _draft != null; }
    }

    /// <summary>Uncommitted preview, or the stored value when none is held.</summary>
    public string DisplayedValue
    {
        get { lock (_gate) return _draft ?? ReadStored(); }
    }

    public string CommittedValue => ReadStored();

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>
    /// Raised once when the stored value actually changes. Not raised for an unchanged commit,
    /// a preview, a refused write, or a stale completion after this registration was replaced.
    /// </summary>
    public event Action<SettingDescriptor>? ValueChanged
    {
        add { lock (_gate) _valueChanged += value; }
        remove { lock (_gate) _valueChanged -= value; }
    }

    public SettingDescriptor Describe()
    {
        lock (_gate)
            return BuildDescriptor(_draft);
    }

    /// <summary>
    /// Hold a local preview without persisting or sending a network write. Used for slider drag
    /// and uncommitted text.
    /// </summary>
    public SettingWriteResult Preview(string? text)
    {
        lock (_gate)
        {
            if (_disposed) return SettingWriteResult.Failed(ReadStored(), "this setting is no longer registered");
            string stored = ReadStored();
            if (!CanEdit(out var error, out _))
                return SettingWriteResult.Refused(stored, error);
            if (!TryValidate(text, out _, out error))
                return SettingWriteResult.Refused(stored, error);
            _draft = text ?? "";
            _state = SettingWriteState.Preview;
            return SettingWriteResult.Preview(_draft);
        }
    }

    /// <summary>
    /// Persist the given text, or the current preview when <paramref name="text"/> is null,
    /// through the permitted edit path.
    /// </summary>
    public SettingWriteResult Commit(string? text = null)
    {
        Action<SettingDescriptor>? listeners;
        SettingDescriptor? snapshot;
        SettingWriteResult result;
        IDisposable? previousRequest = null;
        lock (_gate)
        {
            if (_disposed) return SettingWriteResult.Failed(ReadStored(), "this setting is no longer registered");
            string candidate = text ?? _draft ?? ReadStored();
            result = SubmitCore(candidate, out listeners, out snapshot, out previousRequest);
        }

        previousRequest?.Dispose();
        Raise(listeners, snapshot);
        return result;
    }

    /// <summary>Discard uncommitted edits and cancel key capture. Does not undo applied writes.</summary>
    public SettingWriteResult Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return SettingWriteResult.Failed("", "this setting is no longer registered");
            _draft = null;
            if (_state == SettingWriteState.Preview || _state == SettingWriteState.Idle)
                _state = SettingWriteState.Idle;
            if (_keybind != null) Keybinds.CancelRebind();
            return SettingWriteResult.Idle(ReadStored());
        }
    }

    /// <summary>Same as <see cref="Cancel"/>: Escape discards uncommitted edits and key capture.</summary>
    public SettingWriteResult Escape() => Cancel();

    /// <summary>Write the recorded default through the same validation and permissions as any other write.</summary>
    public SettingWriteResult Reset()
    {
        Action<SettingDescriptor>? listeners;
        SettingDescriptor? snapshot;
        SettingWriteResult result;
        IDisposable? previousRequest = null;
        lock (_gate)
        {
            if (_disposed) return SettingWriteResult.Failed(ReadStored(), "this setting is no longer registered");
            result = SubmitCore(_defaultValue, out listeners, out snapshot, out previousRequest);
        }

        previousRequest?.Dispose();
        Raise(listeners, snapshot);
        return result;
    }

    internal void TrackServer(IDisposable registration)
    {
        lock (_gate) _serverRegistration = registration;
    }

    public void Dispose()
    {
        IDisposable? request;
        IDisposable? registration;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            request = _serverRequest;
            registration = _serverRegistration;
            _serverRequest = null;
            _serverRegistration = null;
            _draft = null;
            _valueChanged = null;
            _state = SettingWriteState.Failed;
        }

        request?.Dispose();
        registration?.Dispose();
        SettingsCatalog.DetachBinding(this);
    }

    internal SettingPresentation Presentation => _presentation;

    internal ConfigEntryBase? Entry => _entry;

    internal bool Matches(string pluginId, string section, string key) =>
        string.Equals(_pluginId, pluginId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_section, section, StringComparison.Ordinal)
        && string.Equals(_key, key, StringComparison.Ordinal);

    internal void CompleteServerForTests(SettingWriteResult result)
    {
        Action<SettingDescriptor>? listeners = null;
        SettingDescriptor? snapshot = null;
        lock (_gate)
        {
            if (_disposed) return;
            ApplyServerCompletion(result, out listeners, out snapshot);
        }

        Raise(listeners, snapshot);
    }

    private SettingWriteResult SubmitCore(
        string candidate,
        out Action<SettingDescriptor>? listeners,
        out SettingDescriptor? snapshot,
        out IDisposable? previousRequest)
    {
        listeners = null;
        snapshot = null;
        previousRequest = _serverRequest;
        _serverRequest = null;

        string stored = ReadStored();
        if (!CanEdit(out var error, out var path))
        {
            _state = SettingWriteState.Refused;
            return SettingWriteResult.Refused(stored, error);
        }

        if (!TryValidate(candidate, out _, out error))
        {
            _state = SettingWriteState.Refused;
            return SettingWriteResult.Refused(stored, error);
        }

        if (path == SettingEditPath.ServerRequest)
            return SendServer(candidate, stored);

        return WriteLocal(candidate, stored, out listeners, out snapshot);
    }

    private SettingWriteResult WriteLocal(
        string candidate,
        string stored,
        out Action<SettingDescriptor>? listeners,
        out SettingDescriptor? snapshot)
    {
        listeners = null;
        snapshot = null;
        var row = AsRow();
        if (!row.TryWrite(candidate, out var error))
        {
            _state = SettingWriteState.Refused;
            return SettingWriteResult.Refused(ReadStored(), error);
        }

        _draft = null;
        string now = ReadStored();
        _state = SettingWriteState.Accepted;
        if (!string.Equals(now, _lastRaised, StringComparison.Ordinal))
        {
            _lastRaised = now;
            listeners = _valueChanged;
            snapshot = BuildDescriptor(null);
        }

        return SettingWriteResult.Accepted(now);
    }

    private SettingWriteResult SendServer(string candidate, string stored)
    {
        int generation = Generation;
        _state = SettingWriteState.Pending;
        _lastError = "";
        try
        {
            _serverRequest = ServerSettings.Request(Id, candidate, stored, result =>
            {
                Action<SettingDescriptor>? listeners = null;
                SettingDescriptor? snapshot = null;
                lock (_gate)
                {
                    if (_disposed || Generation != generation) return;
                    var mapped = result.Success
                        ? SettingWriteResult.Accepted(result.Value)
                        : string.IsNullOrEmpty(result.Error)
                            ? SettingWriteResult.Failed(ReadStored(), "The request failed.")
                            : MapServerFailure(result);
                    ApplyServerCompletion(mapped, out listeners, out snapshot);
                }

                Raise(listeners, snapshot);
            });
        }
        catch (Exception e)
        {
            _state = SettingWriteState.Failed;
            _lastError = e.Message;
            return SettingWriteResult.Failed(stored, e.Message);
        }

        if (_state != SettingWriteState.Pending)
            return new SettingWriteResult(_state, ReadStored(), _lastError);

        _draft = candidate;
        return SettingWriteResult.Pending(stored);
    }

    private static SettingWriteResult MapServerFailure(ServerSettingResult result)
    {
        string error = result.Error ?? "";
        bool failed = error.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("did not accept", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("No server confirmation", StringComparison.OrdinalIgnoreCase) >= 0;
        return failed
            ? SettingWriteResult.Failed(result.Value, error)
            : SettingWriteResult.Refused(result.Value, error);
    }

    private void ApplyServerCompletion(
        SettingWriteResult result,
        out Action<SettingDescriptor>? listeners,
        out SettingDescriptor? snapshot)
    {
        listeners = null;
        snapshot = null;
        _serverRequest = null;
        _state = result.State;
        _lastError = result.Error;
        if (result.State == SettingWriteState.Accepted)
        {
            _draft = null;
            // The client value changes only through ConfigSync. Do not write the server
            // value into the local file here.
            if (!string.Equals(result.Value, _lastRaised, StringComparison.Ordinal))
            {
                _lastRaised = result.Value;
                listeners = _valueChanged;
                snapshot = BuildDescriptor(null);
            }
        }
    }

    private bool CanEdit(out string error, out SettingEditPath path)
    {
        path = ResolveEditPath();
        if (path == SettingEditPath.None)
        {
            if (_foreign && !SettingsCatalog.AllowForeignEdits)
            {
                error = "this setting belongs to another mod; Progenitor cannot tell whether its "
                    + "server syncs it, so it is shown but not edited here";
                return false;
            }

            if (IsServerOwned())
            {
                error = "the server owns this value for this session";
                return false;
            }

            error = (_valueType?.Name ?? "this value") + " is not editable here";
            return false;
        }

        error = "";
        return true;
    }

    private bool TryValidate(string? text, out object? parsed, out string error)
    {
        parsed = null;
        if (_keybind != null)
        {
            if (!KeyChord.TryParse(text, out _))
            {
                error = "not a key chord (e.g. \"F10 + Ctrl\")";
                return false;
            }

            error = "";
            return true;
        }

        if (_entry == null || _valueType == null)
        {
            error = "this value is not editable here";
            return false;
        }

        if (EditorHint == SettingsEditorHint.Color)
        {
            if (!SettingsEditors.TryParseColor(text, _presentation.HasAlpha, out _, out error))
                return false;
            parsed = text ?? "";
            return true;
        }

        if (!SettingsEditors.TryParse(text, _valueType, out parsed, out error))
            return false;

        if (SettingsBounds.Check(_entry, parsed) == SettingsBounds.Verdict.Invalid)
        {
            string allowed = SettingsBounds.Describe(_entry);
            error = "outside the values this setting accepts"
                + (allowed.Length > 0 ? " (" + allowed + ")" : "");
            return false;
        }

        if (_presentation.MaxLength is int max && max >= 0 && (text ?? "").Length > max)
        {
            error = "value is longer than " + max + " characters";
            return false;
        }

        return true;
    }

    private SettingEditPath ResolveEditPath()
    {
        if (_disposed) return SettingEditPath.None;
        if (_keybind != null) return SettingEditPath.LocalConfig;
        if (_entry == null) return SettingEditPath.None;
        if (_foreign && !SettingsCatalog.AllowForeignEdits) return SettingEditPath.None;
        if (IsServerOwned())
            return ServerSettings.CanRequest(Id) ? SettingEditPath.ServerRequest : SettingEditPath.None;
        if (SettingsEditors.KindFor(_valueType) == SettingsEditorKind.ReadOnly
            && EditorHint != SettingsEditorHint.Color)
            return SettingEditPath.None;
        return SettingEditPath.LocalConfig;
    }

    private bool IsServerOwned() =>
        _entry != null && (SettingsCatalog.IsSynced(_entry) || ConfigSync.IsLocked(_entry));

    internal SettingsEditorHint EditorHint
    {
        get
        {
            if (_presentation.EditorHint.HasValue) return _presentation.EditorHint.Value;
            if (_keybind != null) return SettingsEditorHint.Keybind;
            return SettingsEditors.HintFor(_valueType);
        }
    }

    internal SettingCommitPolicy CommitPolicy =>
        _presentation.CommitPolicy ?? SettingsEditors.CommitPolicyFor(EditorHint);

    private string ReadStored()
    {
        try
        {
            if (_keybind != null) return _keybind.Chord.ToString();
            if (_entry == null) return "";
            return SettingsEditors.Format(_entry.BoxedValue);
        }
        catch
        {
            return "";
        }
    }

    private SettingsRow AsRow()
    {
        if (_keybind != null) return SettingsRow.ForKeybind(_keybind);
        return SettingsRow.ForEntry(_pluginId, _entry!, IsServerOwned(), _foreign && !SettingsCatalog.AllowForeignEdits);
    }

    private SettingDescriptor BuildDescriptor(string? preview)
    {
        string stored = ReadStored();
        var hint = EditorHint;
        var path = ResolveEditPath();
        string[] allowed = _presentation.Choices ?? AllowedFromEntry();
        string[] labels = _presentation.ChoiceLabels ?? allowed;
        string? min = _presentation.Min;
        string? max = _presentation.Max;
        string? step = _presentation.Step;
        SplitBounds(ref min, ref max);
        return new SettingDescriptor(
            _pluginId,
            _section,
            _key,
            _valueType?.FullName ?? (_keybind != null ? "Keybind" : ""),
            stored,
            _defaultValue,
            _presentation.Help ?? ReadHelp(),
            _presentation.Group ?? _section,
            _presentation.Order,
            hint,
            CommitPolicy,
            IsServerOwned() ? SettingScope.Server : SettingScope.Local,
            path,
            min,
            max,
            step,
            _presentation.MaxLength,
            allowed,
            labels,
            _presentation.Units,
            _presentation.HasAlpha,
            path != SettingEditPath.None,
            _presentation.Label);
    }

    private string[] AllowedFromEntry()
    {
        if (_valueType != null && _valueType.IsEnum) return Enum.GetNames(_valueType);
        return _entry != null ? SettingsBounds.Options(_entry, SettingsBounds.Describe(_entry)) : Array.Empty<string>();
    }

    private void SplitBounds(ref string? min, ref string? max)
    {
        if (min != null || max != null || _entry == null) return;
        string bounds = SettingsBounds.Describe(_entry);
        int to = bounds.IndexOf(" to ", StringComparison.Ordinal);
        if (to <= 0) return;
        min = bounds.Substring(0, to).Trim();
        max = bounds.Substring(to + 4).Trim();
    }

    private string ReadHelp()
    {
        if (_keybind != null) return _keybind.Description ?? "";
        if (_entry == null) return "";
        try
        {
            var prop = _entry.GetType().GetProperty("Description");
            var description = prop?.GetValue(_entry);
            var textProp = description?.GetType().GetProperty("Description");
            if (textProp?.GetValue(description) is string text && text.Length > 0)
            {
                int nl = text.IndexOf('\n');
                return (nl < 0 ? text : text.Substring(0, nl)).Trim();
            }
        }
        catch { /* decoration */ }

        return "";
    }

    private void Raise(Action<SettingDescriptor>? listeners, SettingDescriptor? snapshot)
    {
        if (listeners == null || snapshot == null) return;
        if (_raising) return;
        _raising = true;
        try { listeners(snapshot); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings ValueChanged threw: " + e.Message); }
        finally { _raising = false; }
    }

    internal static string ResolveDefault(ConfigEntryBase entry, SettingPresentation presentation)
    {
        if (!string.IsNullOrEmpty(presentation.DefaultValue)) return presentation.DefaultValue!;
        try
        {
            var value = entry.GetType().GetProperty("DefaultValue")?.GetValue(entry);
            if (value != null) return SettingsEditors.Format(value);
        }
        catch { /* shim has no DefaultValue */ }

        try { return SettingsEditors.Format(entry.BoxedValue); }
        catch { return ""; }
    }
}

internal sealed class SettingActionBinding : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<string>, SettingActionResult> _handler;
    private readonly Func<bool>? _available;
    private readonly object? _owner;
    private bool _busy;
    private bool _disposed;
    private Action<SettingActionDescriptor>? _changed;

    internal SettingActionBinding(
        string pluginId,
        string id,
        Func<IReadOnlyList<string>, SettingActionResult> handler,
        SettingActionSpec? spec,
        object? owner,
        int generation)
    {
        PluginId = pluginId ?? "";
        Id = id ?? "";
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Spec = spec ?? new SettingActionSpec();
        _available = Spec.IsAvailable;
        _owner = owner;
        Generation = generation;
    }

    internal int Generation { get; }

    internal string PluginId { get; }

    internal string Id { get; }

    internal string SettingId => PluginId + "/" + SettingBinding.ActionSection + "/" + Id;

    internal object? Owner => _owner;

    internal SettingActionSpec Spec { get; }

    internal bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    internal event Action<SettingActionDescriptor>? Changed
    {
        add { lock (_gate) _changed += value; }
        remove { lock (_gate) _changed -= value; }
    }

    internal SettingActionDescriptor Describe()
    {
        lock (_gate)
        {
            return new SettingActionDescriptor(
                PluginId,
                Id,
                Spec.Label ?? Id,
                Spec.Help ?? "",
                Spec.Group ?? "",
                Spec.Order,
                Owner?.ToString() ?? PluginId,
                !_disposed && (_available?.Invoke() ?? true),
                _busy,
                Spec.ArgumentNames ?? Array.Empty<string>(),
                Spec.ArgumentTypes ?? Array.Empty<string>(),
                Spec.ArgumentRequired ?? Array.Empty<bool>(),
                Spec.ArgumentDefaults ?? Array.Empty<string>());
        }
    }

    internal SettingActionResult Invoke(IReadOnlyList<string> arguments, int generation)
    {
        if (generation != Generation) return SettingActionResult.StaleGeneration();
        lock (_gate)
        {
            if (_disposed) return SettingActionResult.StaleGeneration();
            if (_available != null && !_available())
                return SettingActionResult.NotAvailable("This action is not available.");
            if (_busy) return SettingActionResult.BusyAlready();
            _busy = true;
        }

        SettingActionResult result;
        try { result = _handler(arguments ?? Array.Empty<string>()); }
        catch (Exception e) { result = SettingActionResult.Fail(e.Message); }

        Action<SettingActionDescriptor>? listeners;
        SettingActionDescriptor snapshot;
        lock (_gate)
        {
            _busy = false;
            listeners = _changed;
            snapshot = DescribeUnlocked();
        }

        try { listeners?.Invoke(snapshot); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings action callback threw: " + e.Message); }
        return result;
    }

    private SettingActionDescriptor DescribeUnlocked() =>
        new(
            PluginId,
            Id,
            Spec.Label ?? Id,
            Spec.Help ?? "",
            Spec.Group ?? "",
            Spec.Order,
            Owner?.ToString() ?? PluginId,
            !_disposed && (_available?.Invoke() ?? true),
            _busy,
            Spec.ArgumentNames ?? Array.Empty<string>(),
            Spec.ArgumentTypes ?? Array.Empty<string>(),
            Spec.ArgumentRequired ?? Array.Empty<bool>(),
            Spec.ArgumentDefaults ?? Array.Empty<string>());

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _busy = false;
            _changed = null;
        }

        SettingsCatalog.DetachAction(this);
    }
}
