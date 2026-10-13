using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>TEXTUNDO1: what one recorded edit was, for coalescing and the bridge readback.</summary>
internal enum UiTextEditKind
{
    /// <summary>Not an edit the history knows: recorded as its own step.</summary>
    Other = 0,

    /// <summary>Typed characters (and Enter's line break in a text area).</summary>
    Typing = 1,

    /// <summary>Backspace with no selection.</summary>
    Backspace = 2,

    /// <summary>Delete with no selection.</summary>
    Delete = 3,

    /// <summary>Backspace or Delete over a selection.</summary>
    DeleteSelection = 4,

    /// <summary>Ctrl+V.</summary>
    Paste = 5,

    /// <summary>Ctrl+X.</summary>
    Cut = 6,
}

/// <summary>What Ctrl plus a letter asks of the undo history.</summary>
internal enum UiUndoShortcut
{
    None = 0,
    Undo = 1,
    Redo = 2,
}

/// <summary>
/// TEXTUNDO1. Per-field undo and redo for the framework's owned text fields, game-free so the step
/// rules, the bounds and the lifetime have tests that do not need TMP.
///
/// <para><b>Steps.</b> Each entry is a delta: where the change starts, the text it removed, the text it
/// inserted, and the caret and selection before and after. Consecutive typing merges into one step
/// until a word boundary (a whitespace or punctuation character typed after it ends the word; the next
/// word character starts a new step), a pause of <see cref="CoalesceMs"/> or more, a caret move or a
/// selection (<see cref="NoteCaret"/>), or an undo or redo. Backspace runs and Delete runs merge the
/// same way. Paste, cut, deleting a selection and anything else are always their own step.</para>
///
/// <para><b>Bounds.</b> At most <see cref="MaxEntries"/> undo entries and <see cref="MaxChars"/>
/// characters (removed plus inserted, undo and redo together); the oldest undo entries go first. An
/// edit larger than the character bound on its own empties the history rather than breaking it.</para>
///
/// <para><b>Lifetime.</b> The owner clears it when the field loses focus and when the field's text was
/// replaced by anything other than this history's own edits (<see cref="Observe"/>: a commit's
/// trimming, a sync, a reset, a server echo). A new edit clears the redo entries.</para>
/// </summary>
internal sealed class UiTextUndoHistory
{
    /// <summary>Default entry bound.</summary>
    internal const int DefaultMaxEntries = 100;

    /// <summary>Default character bound: 64 K UTF-16 code units (128 KB) per field.</summary>
    internal const int DefaultMaxChars = 64 * 1024;

    /// <summary>A pause this long (ms) between two keystrokes starts a new step.</summary>
    internal const long CoalesceMs = 1000;

    private sealed class Entry
    {
        public UiTextEditKind Kind;
        public int Start;
        public string Removed = "";
        public string Inserted = "";
        public int BeforeCaret;
        public int BeforeAnchor;
        public int AfterCaret;
        public int AfterAnchor;
        public long LastMs;

        public int Chars => Removed.Length + Inserted.Length;
    }

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    private string? _lastText;
    private bool _open;

    internal UiTextUndoHistory(int maxEntries = DefaultMaxEntries, int maxChars = DefaultMaxChars)
    {
        MaxEntries = Math.Max(1, maxEntries);
        MaxChars = Math.Max(1, maxChars);
    }

    internal int MaxEntries { get; }

    internal int MaxChars { get; }

    internal int UndoDepth => _undo.Count;

    internal int RedoDepth => _redo.Count;

    /// <summary>Characters held by every undo and redo entry (removed plus inserted).</summary>
    internal int TotalChars { get; private set; }

    /// <summary>The kind of the newest undo step, or <c>none</c>.</summary>
    internal string LastKind => _undo.Count == 0 ? "none" : KindName(_undo[_undo.Count - 1].Kind);

    /// <summary>Entries dropped to stay inside the bounds, since this history was made.</summary>
    internal long Evicted { get; private set; }

    /// <summary>Undo or redo requests refused because the field would reject the restored text.</summary>
    internal long Refused { get; private set; }

    /// <summary>Why the history was last emptied (<c>blur</c>, <c>external</c>, …), or empty.</summary>
    internal string LastClear { get; private set; } = "";

    internal static string KindName(UiTextEditKind kind) => kind switch
    {
        UiTextEditKind.Typing => "typing",
        UiTextEditKind.Backspace => "backspace",
        UiTextEditKind.Delete => "delete",
        UiTextEditKind.DeleteSelection => "delete-selection",
        UiTextEditKind.Paste => "paste",
        UiTextEditKind.Cut => "cut",
        _ => "other",
    };

    /// <summary>Forget everything. <paramref name="reason"/> is reported by <see cref="LastClear"/>.</summary>
    internal void Clear(string reason)
    {
        if (_undo.Count > 0 || _redo.Count > 0) LastClear = reason ?? "";
        _undo.Clear();
        _redo.Clear();
        TotalChars = 0;
        _lastText = null;
        _open = false;
    }

    /// <summary>
    /// The field's text before an edit or an undo: when it is not the text this history last left, the
    /// field was changed from outside (a sync, a reset, a commit's trimming, a server echo) and the
    /// history no longer describes it, so it is cleared. True when it cleared.
    /// </summary>
    internal bool Observe(string? currentText)
    {
        if (_lastText == null) return false;
        if (string.Equals(_lastText, currentText ?? "", StringComparison.Ordinal)) return false;
        Clear("external");
        return true;
    }

    /// <summary>A caret move or a selection with the text unchanged: the open step ends.</summary>
    internal void NoteCaret(UiTextEditState before, UiTextEditState after)
    {
        if (before.Caret != after.Caret || before.Anchor != after.Anchor) _open = false;
    }

    /// <summary>End the open step (a pause the caller noticed, a shortcut that changed nothing).</summary>
    internal void Seal() => _open = false;

    /// <summary>
    /// Record one edit from <paramref name="before"/> to <paramref name="after"/>. Unchanged text records
    /// nothing (a caret move goes to <see cref="NoteCaret"/>). Clears the redo entries.
    /// </summary>
    internal void Record(UiTextEditState before, UiTextEditState after, UiTextEditKind kind, long nowMs)
    {
        before = UiTextEdit.Normalize(before);
        after = UiTextEdit.Normalize(after);
        if (string.Equals(before.Text, after.Text, StringComparison.Ordinal))
        {
            NoteCaret(before, after);
            return;
        }

        Observe(before.Text);
        var delta = Diff(before, after);
        delta.Kind = kind;
        delta.BeforeCaret = before.Caret;
        delta.BeforeAnchor = before.Anchor;
        delta.AfterCaret = after.Caret;
        delta.AfterAnchor = after.Anchor;
        delta.LastMs = nowMs;

        if (_redo.Count > 0)
        {
            for (int i = 0; i < _redo.Count; i++) TotalChars -= _redo[i].Chars;
            _redo.Clear();
        }

        var top = _undo.Count > 0 ? _undo[_undo.Count - 1] : null;
        if (top != null && _open && TryMerge(top, delta))
        {
            top.AfterCaret = delta.AfterCaret;
            top.AfterAnchor = delta.AfterAnchor;
            top.LastMs = nowMs;
        }
        else
        {
            _undo.Add(delta);
            TotalChars += delta.Chars;
        }

        _open = kind is UiTextEditKind.Typing or UiTextEditKind.Backspace or UiTextEditKind.Delete;
        _lastText = after.Text;
        Evict();
    }

    /// <summary>
    /// Undo the newest step from <paramref name="current"/> (the field's state now). False when there is
    /// nothing to undo, the text is not what the history last left (it is then cleared), or
    /// <paramref name="accepts"/> refuses the restored text (the step is kept).
    /// </summary>
    internal bool TryUndo(UiTextEditState current, Func<string, bool>? accepts, out UiTextEditState restored)
    {
        restored = UiTextEdit.Normalize(current);
        Observe(restored.Text);
        _open = false;
        if (_undo.Count == 0) return false;
        var e = _undo[_undo.Count - 1];
        string text = restored.Text;
        if (!Holds(text, e.Start, e.Inserted))
        {
            Clear("mismatch");
            return false;
        }

        string next = text.Substring(0, e.Start) + e.Removed + text.Substring(e.Start + e.Inserted.Length);
        if (accepts != null && !accepts(next))
        {
            Refused++;
            return false;
        }

        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(e);
        _lastText = next;
        restored = UiTextEdit.Normalize(new UiTextEditState(next, e.BeforeCaret, e.BeforeAnchor));
        return true;
    }

    /// <summary>Redo the newest undone step; the mirror of <see cref="TryUndo"/>.</summary>
    internal bool TryRedo(UiTextEditState current, Func<string, bool>? accepts, out UiTextEditState restored)
    {
        restored = UiTextEdit.Normalize(current);
        Observe(restored.Text);
        _open = false;
        if (_redo.Count == 0) return false;
        var e = _redo[_redo.Count - 1];
        string text = restored.Text;
        if (!Holds(text, e.Start, e.Removed))
        {
            Clear("mismatch");
            return false;
        }

        string next = text.Substring(0, e.Start) + e.Inserted + text.Substring(e.Start + e.Removed.Length);
        if (accepts != null && !accepts(next))
        {
            Refused++;
            return false;
        }

        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(e);
        _lastText = next;
        restored = UiTextEdit.Normalize(new UiTextEditState(next, e.AfterCaret, e.AfterAnchor));
        return true;
    }

    /// <summary>
    /// Ctrl+Z is undo; Ctrl+Y and Ctrl+Shift+Z are redo. Anything else is not the history's.
    /// </summary>
    internal static UiUndoShortcut ShortcutFor(char letter, bool shift)
    {
        char key = char.ToLowerInvariant(letter);
        if (key == 'z') return shift ? UiUndoShortcut.Redo : UiUndoShortcut.Undo;
        if (key == 'y') return UiUndoShortcut.Redo;
        return UiUndoShortcut.None;
    }

    /// <summary>
    /// The kind of edit a decided key makes from <paramref name="before"/>: Backspace and Delete over a
    /// selection delete it as one step; Ctrl+V and Ctrl+X are paste and cut.
    /// </summary>
    internal static UiTextEditKind KindFor(UiTypedCommand command, UiTextEditState before)
    {
        switch (command.Kind)
        {
            case UiTypedCommandKind.Insert:
                return UiTextEditKind.Typing;
            case UiTypedCommandKind.Backspace:
                return before.HasSelection ? UiTextEditKind.DeleteSelection : UiTextEditKind.Backspace;
            case UiTypedCommandKind.Delete:
                return before.HasSelection ? UiTextEditKind.DeleteSelection : UiTextEditKind.Delete;
            case UiTypedCommandKind.Shortcut:
                char key = char.ToLowerInvariant(command.Char);
                return key == 'v' ? UiTextEditKind.Paste : key == 'x' ? UiTextEditKind.Cut : UiTextEditKind.Other;
            default:
                return UiTextEditKind.Other;
        }
    }

    /// <summary>
    /// Whether the field would hold a restored <paramref name="text"/> now: within its current
    /// <paramref name="maxLength"/>. Every text the history restores is one this field held before, so
    /// the typing filter and the single-line rule (which judge characters as they are typed, and which a
    /// stored value such as a number box's starting value need not pass) are not applied again; the limit
    /// is, because it can shrink while the field is focused (a server value's limit).
    /// </summary>
    internal static bool FieldAccepts(string? text, int maxLength)
    {
        string s = text ?? "";
        return maxLength <= 0 || maxLength == int.MaxValue || s.Length <= maxLength;
    }

    /// <summary>A character that ends a typed word: whitespace, punctuation or a symbol.</summary>
    internal static bool IsBoundary(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool TryMerge(Entry top, Entry next)
    {
        if (top.Kind != next.Kind) return false;
        if (next.LastMs - top.LastMs >= CoalesceMs || next.LastMs < top.LastMs) return false;
        switch (next.Kind)
        {
            case UiTextEditKind.Typing:
            {
                if (next.Removed.Length != 0 || next.Inserted.Length == 0) return false;
                if (next.Start != top.Start + top.Inserted.Length) return false;
                // A word character typed after a boundary starts the next word's step.
                if (top.Inserted.Length > 0 && IsBoundary(top.Inserted[top.Inserted.Length - 1]) && !IsBoundary(next.Inserted[0]))
                    return false;
                top.Inserted += next.Inserted;
                return true;
            }
            case UiTextEditKind.Backspace:
            {
                if (next.Inserted.Length != 0 || top.Inserted.Length != 0) return false;
                if (next.Start + next.Removed.Length != top.Start) return false;
                top.Removed = next.Removed + top.Removed;
                top.Start = next.Start;
                return true;
            }
            case UiTextEditKind.Delete:
            {
                if (next.Inserted.Length != 0 || top.Inserted.Length != 0) return false;
                if (next.Start != top.Start) return false;
                top.Removed += next.Removed;
                return true;
            }
            default:
                return false;
        }
    }

    private void Evict()
    {
        // Recount after a merge grew the top entry.
        int total = 0;
        for (int i = 0; i < _undo.Count; i++) total += _undo[i].Chars;
        for (int i = 0; i < _redo.Count; i++) total += _redo[i].Chars;
        TotalChars = total;
        while (_undo.Count > 0 && (_undo.Count > MaxEntries || TotalChars > MaxChars))
        {
            TotalChars -= _undo[0].Chars;
            _undo.RemoveAt(0);
            Evicted++;
        }

        if (_undo.Count == 0 && _redo.Count == 0)
        {
            _open = false;
            _lastText = null;
        }
    }

    private static bool Holds(string text, int start, string part) =>
        start >= 0 && start + part.Length <= text.Length && string.CompareOrdinal(text, start, part, 0, part.Length) == 0;

    /// <summary>
    /// The changed region between two states: the common prefix (never past either selection start, so
    /// an insert lands where the caret was) and the common suffix after it.
    /// </summary>
    private static Entry Diff(UiTextEditState before, UiTextEditState after)
    {
        string a = before.Text;
        string b = after.Text;
        int cap = Math.Min(Math.Min(a.Length, b.Length), Math.Min(before.SelStart, after.SelStart));
        int prefix = 0;
        while (prefix < cap && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        int maxSuffix = Math.Min(a.Length, b.Length) - prefix;
        while (suffix < maxSuffix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        return new Entry
        {
            Start = prefix,
            Removed = a.Substring(prefix, a.Length - prefix - suffix),
            Inserted = b.Substring(prefix, b.Length - prefix - suffix),
        };
    }
}
