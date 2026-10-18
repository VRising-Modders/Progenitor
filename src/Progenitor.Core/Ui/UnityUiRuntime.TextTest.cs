#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// TEXTTEST1: the Core half of the dev bridge's text-field test ops (<c>mods_ui field_focus</c>,
/// <c>field_blur</c>, <c>field_submit</c>, <c>type</c>, <c>paste</c>, <c>perf</c>). Dev reaches this type
/// by reflection (Core grants Dev no <c>InternalsVisibleTo</c>); it is not a plugin API. Main thread.
///
/// <para>Everything goes through the paths a player's input takes: focus is the click's
/// <c>FocusOwnedField</c>, typed text joins the OS text drained from Bootstrap's queue in the
/// shown-input pump, keys go through <see cref="UiTypedKeys.Decide"/>, and paste is the Ctrl+V
/// path with <see cref="UiTextInjection.Clipboard"/> in place of the system clipboard. No OS input
/// is sent and no window focus is taken. Not exercised: the OS/IME → <c>Keyboard.onTextInput</c>
/// hop into Bootstrap's queue, and TMP's own key handling (armed fields are read-only to TMP).</para>
/// </summary>
internal static class UiTextTestBridge
{
    private static long _acceptedAt;
    private static long _filterAt;
    private static long _limitAt;
    private static long _controlAt;

    /// <summary>Focus the field on the row captioned <paramref name="label"/> as a click on it does.</summary>
    public static Dictionary<string, object?> Focus(string label, string? pane) =>
        UnityUiRuntime.TestFocus(label, pane);

    /// <summary>Take focus away as a click outside every field does (TMP's deselect ends the edit).</summary>
    public static Dictionary<string, object?> Blur() => UnityUiRuntime.TestBlur();

    /// <summary>Enter in a single-line field: TMP's submit, then the end of the edit.</summary>
    public static Dictionary<string, object?> Submit() => UnityUiRuntime.TestSubmit();

    /// <summary>
    /// Queue <paramref name="text"/> (<c>via = os-text</c>) or <paramref name="keys"/> (<c>via = keys</c>)
    /// for the focused field, <paramref name="perFrame"/> items a frame.
    /// </summary>
    public static Dictionary<string, object?> Enqueue(string via, string? text, string[]? keys, int perFrame)
    {
        var refused = UnityUiRuntime.TestRequireFocus();
        if (refused != null) return refused;
        if (UiTextInjection.Pending > 0)
            return Error("an earlier type or paste is still running");
        UiTextInjection.Clear();
        UiTextInjection.PerFrame = Math.Max(1, perFrame);
        int queued = 0;
        if (string.Equals(via, "keys", StringComparison.OrdinalIgnoreCase))
        {
            if (keys == null || keys.Length == 0) return Error("args.keys is required for via=keys");
            var parsed = new List<UiTestChord>(keys.Length);
            foreach (string k in keys)
            {
                if (!UiTextTestInput.TryParseChord(k, out var chord, out string why)) return Error(why);
                parsed.Add(chord);
            }
            if (parsed.Count > UiTextTestInput.MaxTypedItems) return Error("too many keys (at most " + UiTextTestInput.MaxTypedItems + ")");
            foreach (var chord in parsed) UiTextInjection.Keys.Enqueue(chord);
            queued = parsed.Count;
        }
        else if (string.Equals(via, "os-text", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(via))
        {
            string s = text ?? "";
            if (s.Length == 0) return Error("args.text is required for via=os-text");
            if (s.Length > UiTextTestInput.MaxTypedItems) return Error("text longer than " + UiTextTestInput.MaxTypedItems + "; use paste");
            foreach (char c in s) UiTextInjection.Text.Enqueue(c);
            queued = s.Length;
        }
        else return Error("args.via must be os-text or keys");

        Snapshot();
        return new Dictionary<string, object?> { ["ok"] = true, ["queued"] = queued, ["perFrame"] = UiTextInjection.PerFrame };
    }

    /// <summary>
    /// Paste through the real Ctrl+V path with a test clipboard: <paramref name="length"/> generated
    /// characters (<see cref="UiTextTestInput.Generate"/>) when above 0, else <paramref name="text"/>
    /// repeated <paramref name="repeat"/> times.
    /// </summary>
    public static Dictionary<string, object?> BeginPaste(string? text, int repeat, int length, int seed)
    {
        var refused = UnityUiRuntime.TestRequireFocus();
        if (refused != null) return refused;
        if (UiTextInjection.Pending > 0) return Error("an earlier type or paste is still running");
        UiTextInjection.Clear();
        text = length > 0 ? UiTextTestInput.Generate(length, seed) : UiTextTestInput.Repeat(text ?? "", repeat);
        UiTextInjection.Clipboard = text;
        UiTextInjection.Keys.Enqueue(new UiTestChord(UiKey.V, ctrl: true, alt: false, shift: false, meta: false));
        Snapshot();
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["clipboardLength"] = text.Length,
            ["clipboardHash"] = UiTextTestInput.Hash(text),
        };
    }

    public static int Pending() => UiTextInjection.Pending;

    /// <summary>
    /// End the call: drop anything still queued, clear the test clipboard, and report what the
    /// typing paths accepted and rejected since <see cref="Enqueue"/> / <see cref="BeginPaste"/>.
    /// </summary>
    public static Dictionary<string, object?> Finish()
    {
        int left = UiTextInjection.Pending;
        UiTextInjection.Clear();
        var stats = UnityUiRuntime.TypedStats;
        return new Dictionary<string, object?>
        {
            ["notApplied"] = left,
            ["accepted"] = stats.Accepted - _acceptedAt,
            ["rejected"] = new Dictionary<string, object?>
            {
                ["filter"] = stats.RejectedFilter - _filterAt,
                ["limit"] = stats.RejectedLimit - _limitAt,
                ["control"] = UnityUiRuntime.InjectedControlDropped - _controlAt,
                ["readOnly"] = stats.RejectedReadOnly - _readOnlyAt,
            },
            ["field"] = UnityUiRuntime.TestFieldReadback(),
        };
    }

    /// <summary>Clear any injected state (the bridge's failure and timeout paths).</summary>
    public static void Reset() => UiTextInjection.Clear();

    /// <summary>The focused field's text, caret, scroll and clipping, as a readback.</summary>
    public static Dictionary<string, object?> FieldReadback() => UnityUiRuntime.TestFieldReadback();

    /// <summary>
    /// One frame's costs, read at the start of the next frame's drain: the previous frame's own
    /// handler time, the time spent outside Core's frame, the worst handler, and the text-field
    /// counters (TMP text marked dirty with the focused field, caret measurements, caret writes,
    /// scroll writes; process totals, the caller takes differences).
    /// </summary>
    public static Dictionary<string, object?> PerfFrame() => new()
    {
        ["intervalMs"] = UiFrameCost.IntervalMs,
        ["outsideMs"] = UiFrameCost.OutsideMs,
        ["frameMs"] = UiFrameCost.FrameMs,
        ["pumpMs"] = UiFrameCost.PumpMs,
        ["tickMs"] = UiFrameCost.TickMs,
        ["layoutMs"] = UiFrameCost.LayoutMs,
        ["rebuildMs"] = UiFrameCost.RebuildMs,
        ["worstHandler"] = UiFrameCost.WorstHandlerName,
        ["worstHandlerMs"] = UiFrameCost.WorstHandlerMs,
        ["textDirtyBeforePump"] = UnityUiRuntime.FocusedTextDirtyBefore,
        ["textDirtyByPump"] = UnityUiRuntime.FocusedTextDirtyByPump,
        ["measureCalls"] = UnityUiRuntime.CaretMeasureCalls,
        ["caretWrites"] = UnityUiRuntime.CaretTransformWrites,
        ["scrollWrites"] = UnityUiRuntime.TextFieldScrollWrites,
        ["focused"] = UnityUiRuntime.TestFocusedCaption(),
    };

    private static void Snapshot()
    {
        var stats = UnityUiRuntime.TypedStats;
        _acceptedAt = stats.Accepted;
        _filterAt = stats.RejectedFilter;
        _limitAt = stats.RejectedLimit;
        _controlAt = UnityUiRuntime.InjectedControlDropped;
        _readOnlyAt = stats.RejectedReadOnly;
    }

    private static long _readOnlyAt;

    // ---- TEXTEXPAND1 / TEXTLIMIT1 ------------------------------------------------------------------

    /// <summary>
    /// Press the expand button of the settings row captioned <paramref name="label"/> on the drawn page,
    /// then focus the editor's field as a click on it does, so <c>type</c> and <c>paste</c> go there.
    /// </summary>
    public static Dictionary<string, object?> Expand(string label)
    {
        var view = ModSettingsView.Drawn ?? throw new ArgumentException("no mod settings page is drawn; open the mod's page first");
        var d = view.ExpandForBridge(label);
        if (d.TryGetValue("open", out var open) && open is true)
        {
            var focus = UnityUiRuntime.TestFocus("dialog", "dialog");
            d["focus"] = focus;
            d["field"] = UnityUiRuntime.TestFieldReadback();
        }
        d["dialog"] = UiDialog.Describe();
        return d;
    }

    /// <summary>Press Apply on the administrator row captioned <paramref name="label"/> (sends the field's text).</summary>
    public static Dictionary<string, object?> Apply(string label)
    {
        var view = ModSettingsView.Drawn ?? throw new ArgumentException("no mod settings page is drawn; open the mod's page first");
        return view.ApplyForBridge(label);
    }

    /// <summary>FIELDAPPLY1: put <paramref name="text"/> in the row's field, then press its Apply (the same click path).</summary>
    public static Dictionary<string, object?> ApplyText(string label, string text)
    {
        var view = ModSettingsView.Drawn ?? throw new ArgumentException("no mod settings page is drawn; open the mod's page first");
        return view.ApplyForBridge(label, text);
    }

    /// <summary>The administrator row's state: waiting, saved, or the refusal, with the field's and the stored value's lengths.</summary>
    public static Dictionary<string, object?> ApplyStatus(string label)
    {
        var view = ModSettingsView.Drawn ?? throw new ArgumentException("no mod settings page is drawn; open the mod's page first");
        return view.ApplyStatusForBridge(label);
    }

    /// <summary>Wheel notches on the focused multi-line field or the open editor (positive toward the end), or to its top or end.</summary>
    public static Dictionary<string, object?> Scroll(int notches, string? to) => UnityUiRuntime.TestScroll(notches, to);

    /// <summary>One Escape, through the key hook the player's Escape takes (the open editor's Cancel).</summary>
    public static Dictionary<string, object?> Cancel()
    {
        var open = UiDialog.Current;
        string kind = open?.Kind ?? "none";
        Ui.PressEscapeForBridge();
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["was"] = kind,
            ["closed"] = open != null && !open.IsOpen,
            ["handler"] = "Ui.OnBootstrapKey(27, then 17): the Escape policy (dialog first)",
            ["dialog"] = UiDialog.Describe(),
        };
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}

internal sealed partial class UnityUiRuntime
{
    internal static long FocusedTextDirtyBefore;
    internal static long FocusedTextDirtyByPump;

    /// <summary>
    /// TEXTAREA2 perf: whether the focused field's text component is marked for a new layout
    /// (<c>havePropertiesChanged</c>). Sampled around the pump: dirty before it means something else
    /// changed the text since TMP last laid it out; dirty only after it means the pump did.
    /// </summary>
    private static bool FocusedTextDirty()
    {
        var field = _ownedFocusField;
        if (field == null) return false;
        try
        {
            var tmp = field.textComponent;
            return tmp != null && tmp.havePropertiesChanged;
        }
        catch { return false; }
    }

    private static void NoteFocusedTextDirty(bool before, bool after)
    {
        if (before) FocusedTextDirtyBefore++;
        else if (after) FocusedTextDirtyByPump++;
    }

    private static TMP_InputField? _testFocusField;
    private static string _testFocusLabel = "";

    internal static string TestFocusedCaption()
    {
        var field = CaretFocusField();
        if (field == null) return "";
        // The row caption field_focus matched: a composite control's own labels (a number box's
        // "-" and "+") would otherwise be read as its caption.
        if (ReferenceEquals(field, _testFocusField) && _testFocusLabel.Length > 0) return _testFocusLabel;
        return IsSearchInput(field) ? "search" : SafeCaption(field);
    }

    internal static Dictionary<string, object?>? TestRequireFocus()
    {
        var field = CaretFocusField();
        if (field != null) return null;
        return new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = "no focused Mods workspace field; run mods_ui field_focus first",
        };
    }

    internal static Dictionary<string, object?> TestFocus(string label, string? pane)
    {
        PruneArmedFields();
        string want = (label ?? "").Trim();
        if (want.Length == 0) return TestError("args.label is required");
        string where = (pane ?? "").Trim().ToLowerInvariant();
        var dialogRoot = OpenDialogRoot();
        var matches = new List<(TMP_InputField field, string kind, string where)>();

        for (int i = 0; i < ShownPanels.Count; i++)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive) continue;
            bool isDialog = dialogRoot != null && panel.GameObject != null && panel.GameObject.transform == dialogRoot;
            string panelKind = isDialog ? "dialog" : panel.IsSidebar ? "sidebar" : "content";
            if (where.Length > 0 && where != panelKind) continue;
            panel.ForEachRowCaption((go, kind, caption) =>
            {
                if (go == null) return;
                bool hit = string.Equals(caption.Trim(), want, StringComparison.OrdinalIgnoreCase)
                    || (isDialog && string.Equals(want, "dialog", StringComparison.OrdinalIgnoreCase));
                if (!hit) return;
                var field = FirstArmedIn(go);
                if (field != null) matches.Add((field, kind, panelKind));
            });
        }

        if (matches.Count == 0 && string.Equals(want, "search", StringComparison.OrdinalIgnoreCase))
        {
            // SEARCHCLEAR1: the search box drawn now (the Install search on the Install tab), else the Mods search row.
            var drawn = DrawnWorkspaceSearch();
            if (drawn?.Field != null)
                matches.Add((drawn.Field, "search", IsSearchInput(drawn.Field) ? "sidebar" : "content"));
            for (int i = 0; i < _armedFields.Count && matches.Count == 0; i++)
            {
                var f = _armedFields[i].Field;
                if (f != null && IsSearchInput(f)) { matches.Add((f, "search", "sidebar")); break; }
            }
        }

        if (matches.Count == 0)
            return TestError("no armed field on a row captioned '" + want + "'" + (where.Length > 0 ? " in pane " + where : ""));

        var chosen = matches[0];
        if (!FocusOwnedField(chosen.field)) return TestError("the field is not armed");
        _testFocusField = chosen.field;
        _testFocusLabel = want;
        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["label"] = want,
            ["kind"] = chosen.kind,
            ["pane"] = chosen.where,
            ["matches"] = matches.Count,
            ["multiline"] = IsMultiline(chosen.field),
            ["filter"] = FieldFilter(chosen.field).ToString(),
            ["characterLimit"] = FieldMaxLength(chosen.field) == int.MaxValue ? 0 : FieldMaxLength(chosen.field),
            ["handler"] = "UnityUiRuntime.FocusOwnedField (the click path)",
        };
        return result;
    }

    internal static Dictionary<string, object?> TestBlur()
    {
        var field = CaretFocusField();
        string caption = field == null ? "" : SafeCaption(field);
        try
        {
            var system = EventSystem.current;
            if (system != null) system.SetSelectedGameObject(null);
        }
        catch { /* */ }
        NoteOwnedFocus(null);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["was"] = caption,
            ["handler"] = "EventSystem.SetSelectedGameObject(null) + NoteOwnedFocus(null) (a click outside)",
        };
    }

    internal static Dictionary<string, object?> TestSubmit()
    {
        var field = CaretFocusField() ?? OpenEditorField();
        if (field == null) return TestError("no focused field");
        // TEXTEXPAND1: in the expanded editor, submit is its Done button (the dialog's main action).
        if (ReferenceEquals(field, OpenEditorField()) && UiDialog.Current is { } editor)
        {
            if (editor.Content.InputReadOnly) return TestError("the editor is read-only; it has Close only (mods_ui field_cancel or dialog_choose cancel)");
            editor.Submit();
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["handler"] = "the editor's Done (UiDialog.Submit: the length check, the row's validation, then the commit)",
                ["closed"] = !editor.IsOpen,
                ["error"] = editor.Error,
                ["row"] = editor.Key,
                ["editorTitle"] = editor.Content.Title,
            };
        }
        if (IsMultiline(field)) return TestError("Enter starts a line in a text area; use field_blur to commit it");
        SubmitField(field);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["handler"] = "TMP_InputField.onSubmit, then the deselect that ends the edit",
        };
    }

    /// <summary>Enter in a single-line field: TMP raises submit and ends the edit (deselect).</summary>
    private static void SubmitField(TMP_InputField field)
    {
        string text = "";
        try { text = field.text ?? ""; } catch { /* */ }
        try { field.onSubmit?.Invoke(text); } catch { /* */ }
        try
        {
            var system = EventSystem.current;
            if (system != null && system.currentSelectedGameObject == field.gameObject)
                system.SetSelectedGameObject(null);
        }
        catch { /* */ }
        NoteOwnedFocus(null);
    }

    private static TMP_InputField? FirstArmedIn(GameObject go)
    {
        for (int i = 0; i < _armedFields.Count; i++)
        {
            var f = _armedFields[i].Field;
            if (f == null) continue;
            try { if (f.transform.IsChildOf(go.transform)) return f; }
            catch { /* */ }
        }
        return null;
    }

    /// <summary>
    /// TEXTTEST1 readback: the focused field's text (whole when short, else length, hash and both
    /// ends), the caret block of <see cref="DescribeFocusedCaret"/>, the text area's scroll offset,
    /// and whether text reaches outside the field's viewport without a mask to clip it.
    /// </summary>
    internal static Dictionary<string, object?> TestFieldReadback()
    {
        var result = new Dictionary<string, object?>();
        var field = CaretFocusField();
        if (field == null)
        {
            result["focused"] = false;
            return result;
        }

        string text = "";
        try { text = ReadbackText(field, field.text ?? ""); } catch { /* */ }
        result["focused"] = true;
        result["masked"] = IsMaskedField(field);
        result["caption"] = TestFocusedCaption();
        result["length"] = text.Length;
        result["hash"] = UiTextTestInput.Hash(text);
        if (text.Length <= 200) result["text"] = text;
        else
        {
            result["head"] = text.Substring(0, 60);
            result["tail"] = text.Substring(text.Length - 60);
        }

        try { result["caretBlock"] = DescribeFocusedCaret(); } catch (Exception e) { result["caretBlock"] = "read-threw:" + e.Message; }

        try
        {
            var tmp = field.textComponent;
            var viewport = field.textViewport;
            if (tmp != null) result["scrollOffset"] = (float)Math.Round(tmp.rectTransform.anchoredPosition.y, 1);
            bool masked = false;
            try
            {
                var mask = viewport != null ? viewport.GetComponent<RectMask2D>() : null;
                masked = mask != null && mask.enabled;
            }
            catch { masked = false; }
            result["viewportMasked"] = masked;
            if (tmp != null && viewport != null)
            {
                var b = tmp.textBounds;
                var trt = tmp.rectTransform;
                Vector3 top = viewport.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, b.max.y, 0f)));
                Vector3 bottom = viewport.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, b.min.y, 0f)));
                Rect v = viewport.rect;
                float above = Math.Max(0f, top.y - v.yMax);
                float below = Math.Max(0f, v.yMin - bottom.y);
                result["textAboveViewport"] = (float)Math.Round(above, 1);
                result["textBelowViewport"] = (float)Math.Round(below, 1);
                result["drawsOutsideFrame"] = !masked && (above > 0.5f || below > 0.5f);
            }
        }
        catch (Exception e) { result["scrollWhy"] = "read-threw:" + e.Message; }

        var armed = FindArmed(field);
        if (armed != null)
        {
            result["caretSource"] = armed.MeasuredSource;
            result["filter"] = armed.Filter.ToString();
        }
        result["characterLimit"] = FieldMaxLength(field) == int.MaxValue ? 0 : FieldMaxLength(field);
        // TEXTEXPAND1: which lines are in view, the scroll position and the bar (a multi-line field).
        if (IsMultiline(field))
        {
            try { result["view"] = DescribeTextView(field); }
            catch (Exception e) { result["view"] = "read-threw:" + e.Message; }
            result["lineBreaks"] = CountChar(text, '\n');
        }
        result["inEditor"] = ReferenceEquals(field, OpenEditorField());
        // TEXTUNDO1: depth, redo depth, total characters and the newest step's kind.
        try { result["undo"] = DescribeUndo(field); }
        catch (Exception e) { result["undo"] = "read-threw:" + e.Message; }
        return result;
    }

    private static int CountChar(string s, char c)
    {
        int n = 0;
        foreach (char x in s) if (x == c) n++;
        return n;
    }

    private static Dictionary<string, object?> TestError(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}
#endif
