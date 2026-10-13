using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// One finite-choice option. <see cref="Value"/> is the stable identity the binding
/// layer stores; <see cref="Label"/> is what draws.
/// </summary>
internal readonly struct UiChoiceOption : IEquatable<UiChoiceOption>
{
    public UiChoiceOption(string value, string? label = null, string? description = null)
    {
        Value = value ?? "";
        Label = string.IsNullOrEmpty(label) ? Value : label;
        Description = description ?? "";
    }

    public string Value { get; }

    public string Label { get; }

    /// <summary>RADIO1: a radio list option's one-line muted explanation under its label (empty: none).</summary>
    public string Description { get; }

    public bool Equals(UiChoiceOption other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is UiChoiceOption other && Equals(other);

    public override int GetHashCode() => Value != null ? StringComparer.Ordinal.GetHashCode(Value) : 0;
}

/// <summary>
/// Dropdown / finite-choice state. Commit is <see cref="UiCommitPolicy.Immediate"/>:
/// confirming an option writes the value now. A value that is not in the list is
/// kept (not coerced) and labeled as unknown.
/// </summary>
internal sealed class UiChoiceState
{
    internal const UiCommitPolicy Commit = UiCommitPolicy.Immediate;

    internal const string UnknownSuffix = " (not in list)";

    private readonly UiChoiceOption[] _options;
    private string _value;
    private int _highlight;
    private bool _open;

    public UiChoiceState(IReadOnlyList<UiChoiceOption> options, string? value)
    {
        if (options == null || options.Count == 0)
            _options = Array.Empty<UiChoiceOption>();
        else
        {
            var copy = new UiChoiceOption[options.Count];
            for (int i = 0; i < options.Count; i++)
                copy[i] = options[i];
            _options = copy;
        }

        _value = value ?? "";
        _highlight = IndexOf(_value);
        if (_highlight < 0) _highlight = 0;
    }

    public IReadOnlyList<UiChoiceOption> Options => _options;

    public string Value => _value;

    public bool ValueInList => IndexOf(_value) >= 0;

    /// <summary>Readable caption. Unknown values keep the raw string plus a suffix.</summary>
    public string DisplayLabel
    {
        get
        {
            int i = IndexOf(_value);
            if (i >= 0) return _options[i].Label;
            return string.IsNullOrEmpty(_value) ? "" : _value + UnknownSuffix;
        }
    }

    public bool IsOpen => _open;

    public int HighlightIndex => _highlight;

    public string HighlightValue =>
        _options.Length == 0 ? "" : _options[ClampHighlight(_highlight)].Value;

    public string HighlightLabel =>
        _options.Length == 0 ? "" : _options[ClampHighlight(_highlight)].Label;

    public void Open()
    {
        _open = true;
        int i = IndexOf(_value);
        _highlight = i >= 0 ? i : 0;
    }

    public void Close() => _open = false;

    public void ToggleOpen()
    {
        if (_open) Close();
        else Open();
    }

    /// <summary>Keyboard up/down. Wraps. No-op when closed or empty.</summary>
    public bool MoveHighlight(int delta)
    {
        if (!_open || _options.Length == 0 || delta == 0) return false;
        int next = _highlight + delta;
        int n = _options.Length;
        next %= n;
        if (next < 0) next += n;
        if (next == _highlight) return false;
        _highlight = next;
        return true;
    }

    /// <summary>
    /// DROPHOVER1: an option's fill alpha in the open list: the highlighted one (pointer or controller) lit, the current one
    /// faint. Live 2026-10-11: 0.14 and 0.06 read as a light gray slab (the game composes in linear space), the gold caption
    /// faint on it.
    /// </summary>
    internal static float OptionFillAlpha(bool current, bool highlighted) => highlighted ? 0.05f : current ? 0.015f : 0f;

    /// <summary>DROPHOVER1: the description box beside the open list: its width, inner padding, text size and gap from the list.</summary>
    internal const float TipWidth = 300f;

    internal const float TipPad = 10f;

    internal const float TipFontSize = 15f;

    internal const float TipGap = 8f;

    /// <summary>The box's height for its text's measured height: the text and its padding, at least one option row.</summary>
    internal static float TipHeight(float textHeight, float optionHeight) =>
        Math.Max(optionHeight, (float.IsNaN(textHeight) ? 0f : Math.Max(0f, textHeight)) + 2f * TipPad);

    /// <summary>
    /// The pointer is over option <paramref name="index"/> of the open list: the highlight goes there (user, 2026-10-10:
    /// "the selection highlight should follow my hover"). No-op when closed or out of range; true when it moved.
    /// </summary>
    public bool SetHighlight(int index)
    {
        if (!_open || index < 0 || index >= _options.Length || index == _highlight) return false;
        _highlight = index;
        return true;
    }

    /// <summary>Enter on an open list. Commits the highlight immediately.</summary>
    public bool ConfirmHighlight()
    {
        if (!_open || _options.Length == 0) return false;
        return Select(_options[ClampHighlight(_highlight)].Value);
    }

    /// <summary>
    /// Set the underlying value. Unknown values are stored, not snapped to the first
    /// option. Returns true when the stored value changed (a commit).
    /// </summary>
    public bool Select(string? value)
    {
        string next = value ?? "";
        bool changed = !string.Equals(_value, next, StringComparison.Ordinal);
        _value = next;
        int i = IndexOf(_value);
        if (i >= 0) _highlight = i;
        _open = false;
        return changed;
    }

    private int IndexOf(string value)
    {
        for (int i = 0; i < _options.Length; i++)
        {
            if (string.Equals(_options[i].Value, value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private int ClampHighlight(int index)
    {
        if (_options.Length == 0) return 0;
        if (index < 0) return 0;
        if (index >= _options.Length) return _options.Length - 1;
        return index;
    }
}
