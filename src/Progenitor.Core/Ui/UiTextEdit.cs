using System;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// How a settings-kit control writes its value. Stated here so a later binding layer
/// can honor the handoff policy without inventing a second rule per widget.
/// </summary>
internal enum UiCommitPolicy
{
    /// <summary>Discrete choice: toggle, dropdown option, stepper click, keybind land.</summary>
    Immediate = 0,

    /// <summary>Typed text or number: Apply, Enter, or end-edit. Not per keystroke.</summary>
    OnSubmit = 1,

    /// <summary>
    /// Slider drag previews locally and commits once on release. Never a write per frame.
    /// Keyboard nudges are discrete and use <see cref="Immediate"/>.
    /// </summary>
    OnRelease = 2,
}

/// <summary>
/// Caret, selection and the string they describe. Game-free so Backspace, replace
/// and shortcuts have tests that do not need TMP.
/// </summary>
internal readonly struct UiTextEditState : IEquatable<UiTextEditState>
{
    public UiTextEditState(string? text, int caret, int anchor)
    {
        Text = text ?? "";
        Caret = caret;
        Anchor = anchor;
    }

    public string Text { get; }

    /// <summary>Insertion point and the moving end of a selection.</summary>
    public int Caret { get; }

    /// <summary>Where the selection started. Equal to <see cref="Caret"/> means no selection.</summary>
    public int Anchor { get; }

    public int SelStart => Caret < Anchor ? Caret : Anchor;

    public int SelEnd => Caret < Anchor ? Anchor : Caret;

    public bool HasSelection => Caret != Anchor;

    public bool Equals(UiTextEditState other) =>
        Text == other.Text && Caret == other.Caret && Anchor == other.Anchor;

    public override bool Equals(object? obj) => obj is UiTextEditState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Text, Caret, Anchor);
}

/// <summary>What one key did to a field, including a clipboard write for copy/cut.</summary>
internal readonly struct UiTextEditResult
{
    public UiTextEditResult(UiTextEditState state, bool textChanged, bool consumed, string? clipboardSet = null)
    {
        State = state;
        TextChanged = textChanged;
        Consumed = consumed;
        ClipboardSet = clipboardSet;
    }

    public UiTextEditState State { get; }

    public bool TextChanged { get; }

    public bool Consumed { get; }

    public string? ClipboardSet { get; }
}

/// <summary>
/// Ordinary text-field semantics for the shown-input pump. This is the owned path:
/// caret and selection are first-class, a printable character replaces an active
/// selection, Backspace deletes the selection or the character before the caret,
/// and Ctrl/Alt/Meta suppress insertion so Ctrl+A / Ctrl+V cannot also type <c>a</c>
/// / <c>v</c>.
///
/// <para>
/// <b>Where characters come from (TEXTINPUT1).</b> This build's <c>Keyboard</c> exposes
/// <c>add_onTextInput(Il2CppSystem.Action&lt;char&gt;)</c>, not a C# event a collectible
/// Core can subscribe, so Bootstrap subscribes it and Core drains the typed characters
/// (layout, AltGr, dead keys). With an older Bootstrap the pump samples physical keys and
/// maps the US layout (<see cref="UiTypedKeys"/>): letters, digits and their Shift symbols,
/// the punctuation block, the numpad. IME composition never arrives through either path.
/// </para>
/// </summary>
internal static class UiTextEdit
{
    /// <summary>Typed characters commit with the field that owns them (live-text or end-edit).</summary>
    internal const UiCommitPolicy Commit = UiCommitPolicy.OnSubmit;

    internal static UiTextEditState Normalize(UiTextEditState state)
    {
        string text = state.Text ?? "";
        int caret = Clamp(state.Caret, text.Length);
        int anchor = Clamp(state.Anchor, text.Length);
        return new UiTextEditState(text, caret, anchor);
    }

    /// <summary>
    /// Ctrl, Alt or Meta is held: do not insert the physical A–Z / digit. Shortcuts still run.
    /// Shift alone is how capitals are typed and is not a suppressor.
    /// </summary>
    internal static bool SuppressPrintable(bool ctrl, bool alt, bool meta) => ctrl || alt || meta;

    internal static UiTextEditState Insert(UiTextEditState state, string? insertion, int maxLength = int.MaxValue)
    {
        state = Normalize(state);
        string add = insertion ?? "";
        if (add.Length == 0) return state;

        string text = state.Text;
        int start = state.SelStart;
        int end = state.SelEnd;
        int room = maxLength == int.MaxValue ? int.MaxValue : Math.Max(0, maxLength - (text.Length - (end - start)));
        if (room <= 0) return state;
        if (add.Length > room) add = add.Substring(0, room);

        string next = text.Substring(0, start) + add + text.Substring(end);
        int caret = start + add.Length;
        return new UiTextEditState(next, caret, caret);
    }

    /// <summary>
    /// Selection: delete it. Otherwise delete the character before the caret.
    /// For <c>abcd</c> with the caret after <c>b</c> this yields <c>acd</c>, not <c>abc</c>.
    /// </summary>
    internal static UiTextEditState Backspace(UiTextEditState state)
    {
        state = Normalize(state);
        if (state.HasSelection)
            return DeleteSelection(state);
        if (state.Caret <= 0) return state;
        string next = state.Text.Remove(state.Caret - 1, 1);
        int caret = state.Caret - 1;
        return new UiTextEditState(next, caret, caret);
    }

    internal static UiTextEditState Delete(UiTextEditState state)
    {
        state = Normalize(state);
        if (state.HasSelection)
            return DeleteSelection(state);
        if (state.Caret >= state.Text.Length) return state;
        string next = state.Text.Remove(state.Caret, 1);
        return new UiTextEditState(next, state.Caret, state.Caret);
    }

    internal static UiTextEditState Move(UiTextEditState state, int destination, bool extend)
    {
        state = Normalize(state);
        int dest = Clamp(destination, state.Text.Length);
        int anchor = extend ? state.Anchor : dest;
        return new UiTextEditState(state.Text, dest, anchor);
    }

    internal static UiTextEditState Left(UiTextEditState state, bool extend)
    {
        state = Normalize(state);
        if (!extend && state.HasSelection)
            return new UiTextEditState(state.Text, state.SelStart, state.SelStart);
        return Move(state, state.Caret - 1, extend);
    }

    internal static UiTextEditState Right(UiTextEditState state, bool extend)
    {
        state = Normalize(state);
        if (!extend && state.HasSelection)
            return new UiTextEditState(state.Text, state.SelEnd, state.SelEnd);
        return Move(state, state.Caret + 1, extend);
    }

    internal static UiTextEditState Home(UiTextEditState state, bool extend) =>
        Move(state, 0, extend);

    internal static UiTextEditState End(UiTextEditState state, bool extend) =>
        Move(Normalize(state), Normalize(state).Text.Length, extend);

    internal static UiTextEditState SelectAll(UiTextEditState state)
    {
        state = Normalize(state);
        return new UiTextEditState(state.Text, state.Text.Length, 0);
    }

    internal static string SelectedText(UiTextEditState state)
    {
        state = Normalize(state);
        if (!state.HasSelection) return "";
        return state.Text.Substring(state.SelStart, state.SelEnd - state.SelStart);
    }

    internal static UiTextEditResult Copy(UiTextEditState state)
    {
        state = Normalize(state);
        if (!state.HasSelection)
            return new UiTextEditResult(state, false, true);
        return new UiTextEditResult(state, false, true, SelectedText(state));
    }

    internal static UiTextEditResult Cut(UiTextEditState state)
    {
        state = Normalize(state);
        if (!state.HasSelection)
            return new UiTextEditResult(state, false, true);
        string clip = SelectedText(state);
        return new UiTextEditResult(DeleteSelection(state), true, true, clip);
    }

    internal static UiTextEditResult Paste(
        UiTextEditState state, string? clipboard, int maxLength = int.MaxValue, bool multiline = false)
    {
        state = Normalize(state);
        string add = CleanPaste(clipboard, multiline);
        if (add.Length == 0)
            return new UiTextEditResult(state, false, true);
        var next = Insert(state, add, maxLength);
        return new UiTextEditResult(next, next.Text != state.Text, true);
    }

    /// <summary>
    /// Clipboard text as a field may hold it. Single-line: CR and LF are stripped rather than
    /// inserting a line break the layout cannot show. Multi-line (TEXTAREA1): CRLF and a lone CR
    /// become LF, the one line break the typed Enter inserts. Other control characters (NUL, ESC,
    /// …) are dropped either way; Tab is kept.
    /// </summary>
    internal static string CleanPaste(string? clipboard, bool multiline)
    {
        string add = clipboard ?? "";
        if (add.Length == 0) return add;
        add = multiline
            ? add.Replace("\r\n", "\n").Replace('\r', '\n')
            : add.Replace("\r", "").Replace("\n", "");
        StringBuilder? sb = null;
        for (int i = 0; i < add.Length; i++)
        {
            char c = add[i];
            bool keep = !char.IsControl(c) || c == '\t' || (multiline && c == '\n');
            if (keep)
            {
                sb?.Append(c);
                continue;
            }
            if (sb == null)
            {
                sb = new StringBuilder(add.Length);
                sb.Append(add, 0, i);
            }
        }
        return sb == null ? add : sb.ToString();
    }

    /// <summary>
    /// Ctrl (without Alt/Meta) plus A/C/X/V. Any other Ctrl/Alt/Meta letter is consumed
    /// and does not insert.
    /// </summary>
    internal static UiTextEditResult ApplyShortcut(
        UiTextEditState state, char letter, string? clipboard, int maxLength = int.MaxValue, bool multiline = false)
    {
        state = Normalize(state);
        char key = char.ToLowerInvariant(letter);
        switch (key)
        {
            case 'a':
                return new UiTextEditResult(SelectAll(state), false, true);
            case 'c':
                return Copy(state);
            case 'x':
                return Cut(state);
            case 'v':
                return Paste(state, clipboard, maxLength, multiline);
            default:
                return new UiTextEditResult(state, false, true);
        }
    }

    /// <summary>
    /// SETTINGS1 stage 2: Enter (or the keypad Enter) with no Ctrl or Alt starts a new line in a
    /// multi-line field. A single-line field is left to TMP, whose end-of-edit commits it.
    /// </summary>
    internal static bool EnterStartsLine(UiKey key, bool ctrl, bool alt, bool multiline) =>
        multiline && !ctrl && !alt && (key == UiKey.Enter || key == UiKey.NumpadEnter);

    /// <summary>SETTINGS1 stage 2: insert a line break at the caret, replacing any selection.</summary>
    internal static UiTextEditResult Newline(UiTextEditState state, int maxLength = int.MaxValue)
    {
        state = Normalize(state);
        var inserted = Insert(state, "\n", maxLength);
        return new UiTextEditResult(inserted, inserted.Text != state.Text, true);
    }

    /// <summary>Printable insertion that replaces an active selection.</summary>
    internal static UiTextEditResult TypeChar(UiTextEditState state, char c, int maxLength = int.MaxValue)
    {
        state = Normalize(state);
        if (c == '\b')
        {
            var next = Backspace(state);
            return new UiTextEditResult(next, next.Text != state.Text, true);
        }

        if (char.IsControl(c))
            return new UiTextEditResult(state, false, false);

        var inserted = Insert(state, c.ToString(), maxLength);
        return new UiTextEditResult(inserted, inserted.Text != state.Text, true);
    }

    private static UiTextEditState DeleteSelection(UiTextEditState state)
    {
        string next = state.Text.Substring(0, state.SelStart) + state.Text.Substring(state.SelEnd);
        return new UiTextEditState(next, state.SelStart, state.SelStart);
    }

    private static int Clamp(int value, int length)
    {
        if (value < 0) return 0;
        if (value > length) return length;
        return value;
    }
}

/// <summary>
/// TEXTUNDO1 polish: the whitespace a committed text value loses, so what the field shows after a
/// commit is what is stored.
///
/// <para>BepInEx 6 (<c>BepInEx.Core.dll</c>, <c>ConfigFile.Reload</c>, read with the IL viewer) trims every
/// cfg line and then the value after <c>=</c> with <see cref="string.Trim()"/> when it <b>reads</b>
/// the file; <c>Save</c> writes the value as it is. Its string escape (<c>TomlTypeConverter</c>)
/// writes Tab, LF, CR, VT, FF, NUL, BEL and BS as backslash escapes first, so those survive the read;
/// a space, a no-break space and the other Unicode spaces at either end do not. A committed value
/// therefore keeps its trailing space in memory until the next reload, then loses it. Trimming at
/// commit applies the same rule at once, on the client and on the server.</para>
/// </summary>
internal static class UiCommitText
{
    /// <summary>
    /// <paramref name="text"/> without the leading and trailing characters a cfg read drops: whitespace
    /// (<see cref="char.IsWhiteSpace(char)"/>) other than the escaped Tab, LF, CR, VT and FF.
    /// </summary>
    internal static string Trim(string? text)
    {
        string s = text ?? "";
        int start = 0;
        int end = s.Length;
        while (start < end && Drops(s[start])) start++;
        while (end > start && Drops(s[end - 1])) end--;
        return start == 0 && end == s.Length ? s : s.Substring(start, end - start);
    }

    /// <summary>True for a character the cfg read trims from either end of a value.</summary>
    internal static bool Drops(char c) =>
        char.IsWhiteSpace(c) && c != '\t' && c != '\n' && c != '\r' && c != '\v' && c != '\f';
}

/// <summary>
/// TEXTAREA1. Untrusted text inside a rich-text TMP label (cells and scroll lines mix framework
/// markup with listing names, descriptions and values).
/// </summary>
internal static class UiRichText
{
    private const string LiteralLess = "<noparse><</noparse>";

    /// <summary>
    /// <paramref name="text"/> as literal characters in a rich-text label. Each <c>&lt;</c> gets its
    /// own <c>noparse</c> run, so no tag in the text can open; the earlier single
    /// <c>&lt;noparse&gt;…&lt;/noparse&gt;</c> wrapper was closed early by text that itself
    /// contained <c>&lt;/noparse&gt;</c>, after which <c>&lt;size=999&gt;</c> applied.
    /// </summary>
    internal static string Literal(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text!.IndexOf('<') < 0 ? text : text.Replace("<", LiteralLess);
    }
}

/// <summary>
/// Raise a text-changed callback exactly once for a successful write. TMP's
/// <c>text</c> setter may already invoke <c>onValueChanged</c>; invoking again
/// does not throw, so a second raise is invisible unless the count is measured.
/// </summary>
internal static class UiTextChange
{
    /// <summary>
    /// After assigning <paramref name="next"/>, invoke the listeners only when the
    /// setter did not already raise them. Unchanged text is silent.
    /// </summary>
    /// <param name="previous">Value before the write.</param>
    /// <param name="next">Value written.</param>
    /// <param name="setText">Assigns the field. May itself increment <paramref name="raiseCount"/>.</param>
    /// <param name="invokeChanged">Manual raise used only when the setter stayed silent.</param>
    /// <param name="raiseCount">Observed callback count so far.</param>
    /// <returns>How many times the callback ran for this write (0 or 1).</returns>
    internal static int Apply(
        string? previous,
        string? next,
        Action<string> setText,
        Action<string> invokeChanged,
        Func<int> raiseCount)
    {
        if (setText == null) throw new ArgumentNullException(nameof(setText));
        if (invokeChanged == null) throw new ArgumentNullException(nameof(invokeChanged));
        if (raiseCount == null) throw new ArgumentNullException(nameof(raiseCount));

        string from = previous ?? "";
        string to = next ?? "";
        int before = raiseCount();
        setText(to);
        if (from == to)
            return raiseCount() - before;

        if (raiseCount() == before)
            invokeChanged(to);

        int delta = raiseCount() - before;
        return delta < 0 ? 0 : delta;
    }
}

/// <summary>
/// Caret ownership for a <c>readOnly</c> TMP field. This build's
/// <c>TMP_InputField</c> exposes <c>caretPosition</c>, <c>stringPosition</c>,
/// <c>selectionAnchorPosition</c>, <c>selectionFocusPosition</c>,
/// <c>selectionStringAnchorPosition</c> and <c>selectionStringFocusPosition</c>
/// (the IL viewer, <c>Unity.TextMeshPro.dll</c>). Assigning <c>field.text</c> snaps
/// the string caret to 0; the public <c>caretPosition</c> getter is derived from
/// <c>stringPositionInternal</c>, so a write of <c>caretPosition</c> that does
/// not also stick <c>stringPosition</c> reads back as 0. Native processing is
/// off (<c>readOnly</c>), so TMP will not advance the caret for us.
///
/// The owned <see cref="UiTextEditState"/> is the source of truth. Native
/// positions are a visual push after each write.
/// </summary>
internal static class UiFieldCaret
{
    /// <summary>What this build's TMP does when <c>field.text</c> is assigned.</summary>
    internal static UiTextEditState NativeAfterTextAssign(string? text) =>
        new(text ?? "", caret: 0, anchor: 0);

    /// <summary>
    /// Read the live string with the owned caret. Never trust a native caret of 0
    /// after a text assignment — that is the <c>doolb</c> / dead-Backspace path.
    /// A rebuilt field (no owned session for this string) lands at the end.
    /// </summary>
    internal static UiTextEditState Read(string? liveText, UiTextEditState? owned)
    {
        string text = liveText ?? "";
        if (owned.HasValue && owned.Value.Text == text)
            return UiTextEdit.Normalize(new UiTextEditState(text, owned.Value.Caret, owned.Value.Anchor));
        int end = text.Length;
        return new UiTextEditState(text, end, end);
    }

    internal static UiTextEditResult Apply(
        string? liveText,
        UiTextEditState? owned,
        UiKey key,
        bool ctrl,
        bool alt,
        bool meta,
        bool shift,
        string? clipboard,
        int maxLength,
        out UiTextEditState nativeAfterAssign)
    {
        var before = Read(liveText, owned);
        var result = UiTypedKeys.Apply(before, key, ctrl, alt, meta, shift, clipboard, maxLength);
        nativeAfterAssign = NativeAfterTextAssign(result.State.Text);
        return result;
    }
}

/// <summary>
/// Framework-drawn caret for a <c>readOnly</c> TMP field. This build's
/// <c>TMP_InputField</c> still has <c>GenerateCaret</c>, <c>CaretBlink</c>,
/// <c>caretWidth</c>, <c>caretBlinkRate</c> and <c>customCaretColor</c>
/// (the IL viewer, <c>Unity.TextMeshPro.dll</c>), but those fill a mesh only while
/// TMP <c>isFocused</c> (<c>m_AllowInput</c>). Interop stubs have no bodies, so
/// a <c>m_ReadOnly</c> skip inside <c>GenerateCaret</c> cannot be disproved
/// here; the live field already showed no caret with those properties set, and
/// assigning <c>field.text</c> resets <c>stringPosition</c> to 0, so TMP's
/// graphic would not track <see cref="UiFieldCaret"/> anyway. <c>readOnly</c>
/// stays true — turning it off reintroduces the double-write.
///
/// X is the rendered end of the text up to the owned index:
/// <c>textInfo.characterInfo[i].origin</c> / <c>xAdvance</c> (the IL viewer,
/// <c>TMP_CharacterInfo</c>). Blink is a 1 Hz on/off cycle restarted on every
/// owned edit so the bar cannot blink out mid-keystroke.
/// </summary>
internal static class UiOwnedCaret
{
    internal const string Name = "owned-caret";

    internal const int WidthPx = 2;

    /// <summary>Search / input field height from <c>PlaceControl</c>.</summary>
    internal const int FieldHeightPx = 28;

    /// <summary>Vertical inset of the text viewport inside the field.</summary>
    internal const int PadYPx = 4;

    /// <summary>Fallback height: <see cref="FieldHeightPx"/> minus 2× <see cref="PadYPx"/>.</summary>
    internal const int HeightPx = 20;

    /// <summary>
    /// Max |caret center − text-line center| in client pixels. Tighter than
    /// <see cref="PadYPx"/> so a bar sitting on the field's bottom edge (the
    /// UIC5 live miss, 14 px off) cannot pass.
    /// </summary>
    internal const float LineCenterTolerancePx = 4f;

    /// <summary>Full on+off cycle. 1 Hz, 50 % duty.</summary>
    internal const int BlinkPeriodMs = 1000;

    /// <summary>Lit half of <see cref="BlinkPeriodMs"/>.</summary>
    internal const int BlinkOnMs = 500;

    /// <summary>
    /// Typing-reset rule: any owned caret or text change sets the blink cycle
    /// origin to now, so the caret stays solid for <see cref="BlinkOnMs"/>
    /// after the keystroke instead of blinking out mid-edit.
    /// </summary>
    internal const bool RestartBlinkOnEdit = true;

    /// <summary>
    /// Whether the bar is drawn this instant: focused, in the lit half of the blink, and not a
    /// read-only field (TEXTUNDO1 polish: a field that takes no edit shows no insertion point; its
    /// selection and Ctrl+A / Ctrl+C still work).
    /// </summary>
    internal static bool IsLit(bool focused, long nowMs, long cycleOriginMs, bool readOnly = false)
    {
        if (!focused || readOnly) return false;
        long elapsed = nowMs - cycleOriginMs;
        if (elapsed < 0) elapsed = 0;
        return (elapsed % BlinkPeriodMs) < BlinkOnMs;
    }

    /// <summary>
    /// Local X of the insertion point in the text component. Empty or caret 0
    /// uses the first origin (or <paramref name="emptyOrigin"/>). A caret past
    /// the last glyph uses that glyph's <c>xAdvance</c>; otherwise the origin
    /// of the character at the owned index — the same pairing TMP's
    /// <c>GenerateCaret</c> reads from <c>characterInfo</c>.
    /// </summary>
    internal static float LocalX(
        int caret, int characterCount, float[]? origins, float[]? advances, float emptyOrigin)
    {
        if (characterCount <= 0 || origins == null || advances == null) return emptyOrigin;
        int n = characterCount;
        if (n > origins.Length) n = origins.Length;
        if (n > advances.Length) n = advances.Length;
        if (n <= 0) return emptyOrigin;
        if (caret <= 0) return origins[0];
        if (caret >= n) return advances[n - 1];
        return origins[caret];
    }

    /// <summary>
    /// Vertical center of the text rect in the text component's local space.
    /// Not <c>characterInfo</c> ascender/descender: those sit on the glyph
    /// baseline, and a bar whose pivot is the baseline hangs below the line.
    /// </summary>
    internal static float LineCenterY(float rectYMin, float rectYMax) =>
        (rectYMin + rectYMax) * 0.5f;

    /// <summary>
    /// <c>InverseTransformPoint</c> is parent-pivot local. <c>anchoredPosition</c>
    /// is offset from the child's anchor point in the parent rect. They coincide
    /// only when the parent pivot sits on that anchor. The field's
    /// <c>PlaceControl</c> pivot is top-center (0.5, 1); a center-anchored caret
    /// (0.5, 0.5) has its anchor at the rect center. Writing the inverse-transform
    /// point straight into <c>anchoredPosition</c> drops the bar by half the
    /// field height — live, a 20 px bar centered on the bottom edge of a 28 px field.
    /// </summary>
    internal static void AnchoredFromParentLocal(
        float parentLocalX, float parentLocalY,
        float rectXMin, float rectYMin, float rectXMax, float rectYMax,
        float anchorX, float anchorY,
        out float anchoredX, out float anchoredY)
    {
        float ax = rectXMin + (rectXMax - rectXMin) * anchorX;
        float ay = rectYMin + (rectYMax - rectYMin) * anchorY;
        anchoredX = parentLocalX - ax;
        anchoredY = parentLocalY - ay;
    }

    /// <summary>
    /// Parent-local Y of a child's pivot given its anchored Y and the parent rect.
    /// Inverse of the Y half of <see cref="AnchoredFromParentLocal"/>.
    /// </summary>
    internal static float CenterFromAnchored(
        float rectYMin, float rectYMax, float anchorY, float anchoredY)
    {
        float anchorPos = rectYMin + (rectYMax - rectYMin) * anchorY;
        return anchorPos + anchoredY;
    }

    /// <summary>
    /// Convert a parent-local center (y-up, origin at a top pivot: 0 = top,
    /// −height = bottom) to a y-down top of the caret, for containment checks
    /// in the same space as <c>mods_ui rects</c> client pixels.
    /// </summary>
    internal static float TopDownFromParentCenter(float parentLocalCenterY, float caretHeight) =>
        -parentLocalCenterY - caretHeight * 0.5f;

    /// <summary>
    /// Client-pixel / y-down containment: caret top ≥ field top and caret bottom
    /// ≤ field bottom. UIC5's bar (height 20, center on the bottom edge of a
    /// 28 px field) fails this; drawable-only checks do not.
    /// </summary>
    internal static bool ContainedVertically(
        float caretTop, float caretHeight, float fieldTop, float fieldHeight)
    {
        if (caretHeight <= 0f || fieldHeight <= 0f) return false;
        return caretTop >= fieldTop && caretTop + caretHeight <= fieldTop + fieldHeight;
    }

    /// <summary>
    /// True when the caret's vertical center is within
    /// <paramref name="tolerance"/> of the text line's center (same y-down space
    /// as <see cref="ContainedVertically"/>).
    /// </summary>
    internal static bool OnTextLine(
        float caretTop, float caretHeight, float lineCenter, float tolerance)
    {
        if (tolerance < 0f) tolerance = 0f;
        float caretCenter = caretTop + caretHeight * 0.5f;
        float d = caretCenter - lineCenter;
        if (d < 0f) d = -d;
        return d <= tolerance;
    }

    internal static float ClampHeight(float desired, float clipHeight)
    {
        float h = desired < 1f ? HeightPx : desired;
        if (clipHeight > 1f && h > clipHeight) h = clipHeight;
        if (h < 1f) h = 1f;
        return h;
    }

    internal static float ClampCenter(float center, float height, float clipMin, float clipMax)
    {
        float half = height * 0.5f;
        float max = clipMax - half;
        float min = clipMin + half;
        if (min > max) return (clipMin + clipMax) * 0.5f;
        if (center > max) return max;
        if (center < min) return min;
        return center;
    }

    /// <summary>
    /// Same recipe as the search placeholder: assigned, active, enabled,
    /// non-zero alpha, non-zero rect, in front of the fill, non-zero mesh.
    /// Plus focused — a caret on an unfocused field is the failure mode.
    /// Blink-off is alpha 0, so a still capture of the off-phase is not drawable.
    /// </summary>
    internal static bool IsDrawable(
        bool focused,
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int siblingIndex,
        int fillSiblingIndex,
        int meshCount) =>
        focused
        && MenuRowStyle.HintGraphicIsDrawable(
            assigned, active, graphicEnabled, alpha, rectW, rectH,
            siblingIndex, fillSiblingIndex, meshCount);
}

/// <summary>
/// Escape on a workspace search box (the Mods search, the Install search). A focused non-empty box
/// clears; a focused empty box gives up focus (SEARCHCLEAR1, requested 2026-10-09: the second
/// Escape leaves the field, the third closes); a box that does not have focus closes the workspace.
///
/// <para>
/// Production order, one physical press (Bootstrap
/// <c>UiInputContext.Update</c>): EventSystem Cancel may already have dropped
/// TMP <c>isFocused</c>; then <see cref="EscapeKeyCode"/> is dispatched, then
/// <see cref="BackAction"/>. The hide sweep runs before the shown-input pump.
/// Owned focus, not EventSystem selection, is what <see cref="Decide"/> must
/// see, and the clear skip must survive the paired Back dispatch — resetting
/// it in <c>NotifyHiddenByEscape</c> is how one press both cleared and closed.
/// </para>
/// </summary>
internal static class UiSearchEscape
{
    /// <summary>Unity <c>KeyCode.Escape</c>, first of the two <c>InvokeKey</c>s.</summary>
    internal const int EscapeKeyCode = 27;

    /// <summary>
    /// <c>UiInputContext.BackAction</c> / <c>ButtonInputAction.Back</c>. The
    /// same physical Escape also invokes this immediately after
    /// <see cref="EscapeKeyCode"/>.
    /// </summary>
    internal const int BackAction = 17;

    internal const string LogPrefix = "Ui: search-escape ";

    internal enum Decision
    {
        Close = 0,
        Clear = 1,

        /// <summary>SEARCHCLEAR1: the focused box is empty: it gives up focus; the workspace stays.</summary>
        Leave = 2,
    }

    internal static Decision Decide(bool searchFocused, string? text) =>
        !searchFocused ? Decision.Close
        : (text ?? "").Length > 0 ? Decision.Clear
        : Decision.Leave;

    /// <summary>
    /// One live-readable line per physical Escape. Pass on a focused non-empty
    /// field is <c>branch=clear focused=1 length=&gt;0 reason=focused-nonempty</c>.
    /// Leaving an empty focused box is <c>branch=leave … reason=empty</c>.
    /// <c>branch=close focused=0 … reason=unfocused</c> with length still &gt;0
    /// is the EventSystem-Cancel miss: owned focus was not used.
    /// </summary>
    internal static string Format(Decision decision, bool focused, int length, bool alreadyCleared)
    {
        string branch = alreadyCleared ? "skip"
            : decision == Decision.Clear ? "clear"
            : decision == Decision.Leave ? "leave"
            : "close";
        string reason = alreadyCleared ? "already-cleared-this-press"
            : !focused ? "unfocused"
            : length <= 0 ? "empty"
            : "focused-nonempty";
        return LogPrefix
            + "branch=" + branch
            + " focused=" + (focused ? "1" : "0")
            + " length=" + length
            + " reason=" + reason;
    }
}

/// <summary>
/// Skip-hide latch for one physical Escape. <see cref="TryConsume"/> returns
/// true when <c>Hide</c> must no-op. <see cref="Release"/> is key-up, panel
/// close, uninstall — not <c>NotifyHiddenByEscape</c>, which runs between the
/// paired 27 and 17 dispatches of the same press.
/// </summary>
internal sealed class UiEscapeSweep
{
    internal bool Cleared { get; private set; }

    private bool _logged;

    internal bool TryConsume(UiSearchEscape.Decision decision)
    {
        if (Cleared) return true;
        // SEARCHCLEAR1: leaving the box takes the press as clearing it does (the paired Back must not close).
        if (decision is not (UiSearchEscape.Decision.Clear or UiSearchEscape.Decision.Leave)) return false;
        Cleared = true;
        return true;
    }

    /// <summary>One diagnostic line per physical press, not per Hide in the snapshot.</summary>
    internal bool ShouldLog()
    {
        if (_logged) return false;
        _logged = true;
        return true;
    }

    internal void Release()
    {
        Cleared = false;
        _logged = false;
    }
}
