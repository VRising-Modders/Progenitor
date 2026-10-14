using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7 code area (<see cref="UiWidgets.CodeArea"/>): an editable, scrollable multi-line field on the game's
/// settings value box, with line numbers, caller-supplied colors (syntax highlighting) and an error marker on
/// each line with a problem. Word wrap is off: one text line is one numbered line; a line wider than the
/// field is clipped at its right edge (the area does not scroll sideways).
/// </summary>
public interface IUiCodeArea : IUiHandle
{
    /// <summary>
    /// The text. Line breaks are stored as <c>\n</c> (<c>\r\n</c> is converted). Setting it redraws the area and
    /// re-runs the highlighter but does not raise <see cref="OnChanged"/>.
    /// </summary>
    string Value { get; set; }

    /// <summary>The player edited the text: once per edit (live, every keystroke), with the new value.</summary>
    IDisposable OnChanged(Action<string> handler);

    /// <summary>
    /// Color the text with <paramref name="highlighter"/>: it is run now and after every change (an edit or a
    /// <see cref="Value"/> write) and its spans replace the current ones. Null stops highlighting (the spans
    /// stay until replaced). A highlighter that throws is logged once and the previous spans stay.
    /// </summary>
    void SetHighlighter(Func<string, IReadOnlyList<UiTextSpan>>? highlighter);

    /// <summary>
    /// Color the text with <paramref name="spans"/> now. They are offsets into <see cref="Value"/>; they are not
    /// moved by later edits (a highlighter, when set, replaces them at the next change).
    /// </summary>
    void SetSpans(IReadOnlyList<UiTextSpan> spans);

    /// <summary>
    /// Mark lines with errors: a red dot in the gutter (hover it for the message), a faint red band across the
    /// line and a summary line under the area. Lines past the end are shown on the last line; an empty list clears.
    /// Markers stay on their line numbers until replaced.
    /// </summary>
    void SetErrors(IReadOnlyList<UiLineError> errors);

    /// <summary>The errors as shown: sorted, one per line (messages on one line joined), clamped to the text.</summary>
    IReadOnlyList<UiLineError> Errors { get; }

    /// <summary>How many lines the text has (at least 1).</summary>
    int LineCount { get; }

    /// <summary>
    /// <c>widget_read</c>: <c>{kind: codearea, caption, lines, lineCount, valueLength, spans, errors:[{line, message}],
    /// summary, highlighter, enabled, view}</c>; <c>view</c> is the runtime's readback (gutter, overlay, bands) when it draws.
    /// </summary>
    Dictionary<string, object?> Describe();
}

/// <summary>What the code area hands the runtime to draw: the colored text and the error lines.</summary>
internal sealed class CodeAreaFrame
{
    internal CodeAreaFrame(string markup, int lineCount, IReadOnlyList<UiLineError> errors)
    {
        Markup = markup ?? "";
        LineCount = Math.Max(1, lineCount);
        Errors = errors ?? Array.Empty<UiLineError>();
    }

    /// <summary>The overlay's rich text (<see cref="UiCodeAreaModel.Markup"/>).</summary>
    internal string Markup { get; }

    internal int LineCount { get; }

    /// <summary>Normalized (<see cref="UiCodeAreaModel.NormalizeErrors"/>).</summary>
    internal IReadOnlyList<UiLineError> Errors { get; }
}

/// <summary>The runtime capability that decorates a cell row's input cell as a code area.</summary>
internal interface ICodeAreaRuntime
{
    /// <summary>
    /// Decorate the input cell <paramref name="cellKey"/> of <paramref name="row"/>: gutter, colored overlay,
    /// error bands and hover. Null when the row has no such cell or it cannot be decorated (the area then
    /// keeps working as a plain multi-line field).
    /// </summary>
    ICodeAreaView? AttachCodeArea(IRuntimeCellRow row, string cellKey, object? owner);
}

/// <summary>One decorated code area. Dispose removes the decorations (the row's own release also destroys them).</summary>
internal interface ICodeAreaView : IDisposable
{
    bool Alive { get; }

    void Render(CodeAreaFrame frame);

    Dictionary<string, object?> Describe();
}

/// <summary>The <see cref="IUiCodeArea"/> handle: state of record, the cell row it draws in, and its view.</summary>
internal sealed class UiCodeAreaHandle : IUiCodeArea
{
    private static int _serial;

    private readonly IUiLabel? _caption;
    private readonly IUiCellRow _row;
    private readonly IUiCellRow? _summaryRow;
    private readonly string _captionText;
    private readonly int _lines;
    private readonly List<Action<string>> _handlers = new();
    private readonly IDisposable? _changeLease;
    private ICodeAreaView? _view;
    private string _value;
    private IReadOnlyList<UiTextSpan> _spans = Array.Empty<UiTextSpan>();
    private IReadOnlyList<UiLineError> _rawErrors = Array.Empty<UiLineError>();
    private List<UiLineError> _errors = new();
    private Func<string, IReadOnlyList<UiTextSpan>>? _highlighter;
    private bool _highlighterLogged;
    private bool _disposed;
    private string _summary = "";
    private int _renders;

    internal UiCodeAreaHandle(IUiPanel panel, string caption, string initial, int lines)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        _captionText = caption ?? "";
        _lines = UiCodeAreaModel.ClampLines(lines);
        _value = UiCodeAreaModel.Normalize(initial);
        int serial = System.Threading.Interlocked.Increment(ref _serial);
        string key = "codearea-" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_captionText.Length > 0) _caption = panel.Label(_captionText);

        float height = UiCodeAreaModel.HeightFor(_lines);
        // TEXTEXPAND1's editor field: multi-line, the settings value box, the game's scroll bar art.
        var cell = new CellSpec(CellKind.Input, UiCodeAreaModel.CellKey)
            .With(text: _value, width: CellWidth.Fill(200f), minWidth: 200f, skin: CellSkin.ValueBox);
        cell.EditorLines = _lines;
        cell.HeightOverride = height;
        _row = panel.CellRow(new CellRowSpec(key, new[] { cell }, height: height, plain: true));

        var summary = new CellSpec(CellKind.Text, UiCodeAreaModel.SummaryCellKey).With(
            text: "", width: CellWidth.Fill(100f), align: CellAlign.Left, tone: CellTone.Orange,
            wrap: CellWrap.Ellipsis, fontSize: UiCodeAreaModel.SummaryFontSize, vAlign: CellVAlign.Top);
        _summaryRow = panel.CellRow(new CellRowSpec(key + "-summary", new[] { summary }, height: UiCodeAreaModel.SummaryRowHeight, plain: true));

        _changeLease = _row.OnChanged(UiCodeAreaModel.CellKey, OnEdited);

        if (_row.Enabled && _row is UiHandle h && h.Runtime is IRuntimeCellRow runtimeRow)
        {
            try
            {
                if (Ui.CurrentRuntime() is ICodeAreaRuntime runtime)
                    _view = runtime.AttachCodeArea(runtimeRow, UiCodeAreaModel.CellKey, h.Owner);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area decoration threw: " + e.Message); }
        }

        Render();
    }

    // ---- IUiHandle ------------------------------------------------------------------------------

    public string Text
    {
        get => Value;
        set => Value = value;
    }

    public bool Visible
    {
        get => !_disposed && _row.Visible;
        set
        {
            if (_disposed) return;
            if (_caption != null) _caption.Visible = value;
            _row.Visible = value;
            if (_summaryRow != null) _summaryRow.Visible = value;
        }
    }

    public bool Enabled => !_disposed && _row.Enabled;

    public bool IsDisposed => _disposed;

    public UiHandleState State => _disposed ? UiHandleState.Disposed : _row.State;

    public string PluginId => _row.PluginId;

    public string RootName => _row.RootName;

    public bool Interactable
    {
        get => _row.Interactable;
        set
        {
            if (_disposed) return;
            _row.Interactable = value;
        }
    }

    // ---- IUiCodeArea ----------------------------------------------------------------------------

    public string Value
    {
        get => _value;
        set
        {
            if (_disposed) return;
            string next = UiCodeAreaModel.Normalize(value);
            if (string.Equals(next, _value, StringComparison.Ordinal)) return;
            // The value of record first: the field's echo of this write arrives with the same text and is ignored.
            _value = next;
            try { _row.SetCell(UiCodeAreaModel.CellKey, text: next); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area write threw: " + e.Message); }
            Highlight();
            Render();
        }
    }

    public IDisposable OnChanged(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (_disposed) return new ActionDisposable(() => { });
        _handlers.Add(handler);
        return new ActionDisposable(() => _handlers.Remove(handler));
    }

    public void SetHighlighter(Func<string, IReadOnlyList<UiTextSpan>>? highlighter)
    {
        if (_disposed) return;
        _highlighter = highlighter;
        _highlighterLogged = false;
        if (highlighter == null) return;
        Highlight();
        Render();
    }

    public void SetSpans(IReadOnlyList<UiTextSpan> spans)
    {
        if (_disposed) return;
        _spans = Copy(spans);
        Render();
    }

    public void SetErrors(IReadOnlyList<UiLineError> errors)
    {
        if (_disposed) return;
        _rawErrors = errors == null ? Array.Empty<UiLineError>() : new List<UiLineError>(errors);
        Render();
    }

    public IReadOnlyList<UiLineError> Errors => _errors;

    public int LineCount => UiCodeAreaModel.CountLines(_value);

    /// <summary>The spans currently drawn (after normalization), for the readback and the tests.</summary>
    internal IReadOnlyList<UiTextSpan> DrawnSpans => UiCodeAreaModel.NormalizeSpans(_spans, _value.Length);

    /// <summary>The overlay text the last render sent (tests).</summary>
    internal string LastMarkup { get; private set; } = "";

    internal bool HasView => _view != null;

    public Dictionary<string, object?> Describe()
    {
        var errors = new List<Dictionary<string, object?>>(_errors.Count);
        foreach (var e in _errors)
            errors.Add(new Dictionary<string, object?> { ["line"] = e.Line, ["message"] = e.Message });
        var d = new Dictionary<string, object?>
        {
            ["kind"] = "codearea",
            ["key"] = _row.Key,
            ["caption"] = _captionText,
            ["lines"] = _lines,
            ["lineCount"] = LineCount,
            ["valueLength"] = _value.Length,
            ["spans"] = DrawnSpans.Count,
            ["errors"] = errors,
            ["summary"] = _summary,
            ["highlighter"] = _highlighter != null,
            ["wrap"] = false,
            ["enabled"] = Enabled,
            ["renders"] = _renders,
            ["normalizedWrites"] = NormalizedWrites,
        };
        if (_view != null)
        {
            try { d["view"] = _view.Alive ? _view.Describe() : null; }
            catch (Exception e) { d["view"] = new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
        }
        else d["view"] = null;

        return d;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handlers.Clear();
        _highlighter = null;
        try { _changeLease?.Dispose(); } catch { /* */ }
        try { _view?.Dispose(); } catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area view dispose threw: " + e.Message); }
        _view = null;
        try { _summaryRow?.Dispose(); } catch { /* */ }
        try { _row.Dispose(); } catch { /* */ }
        try { _caption?.Dispose(); } catch { /* */ }
    }

    // ---- internals ------------------------------------------------------------------------------

    private void OnEdited(string value)
    {
        if (_disposed) return;
        string raw = value ?? "";
        // Line breaks are stored as \n, typed or pasted (a pasted \r\n or lone \r included).
        string next = UiCodeAreaModel.Normalize(raw);
        bool fieldHoldsCr = !string.Equals(raw, next, StringComparison.Ordinal);
        bool changed = !string.Equals(next, _value, StringComparison.Ordinal);
        // The value of record first, so the field's echo of the write-back below is ignored (no second OnChanged).
        _value = next;
        if (fieldHoldsCr) WriteBackNormalized(next);
        // The echo of a programmatic write (or a repeat of the same text) is not an edit.
        if (!changed) return;
        Highlight();
        Render();
        var snapshot = _handlers.ToArray();
        foreach (var handler in snapshot)
        {
            try { handler(next); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area change handler threw: " + e.Message); }
        }
    }

    /// <summary>
    /// The field held a <c>\r</c>: give it the normalized text. The cell rewrite only writes a differing value
    /// (the caret is moved only by the characters removed), and its echo equals the value of record.
    /// </summary>
    private void WriteBackNormalized(string next)
    {
        NormalizedWrites++;
        try { _row.SetCell(UiCodeAreaModel.CellKey, text: next); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area line-break write-back threw: " + e.Message); }
    }

    /// <summary>How many times an edit carrying <c>\r</c> was written back normalized (readback and tests).</summary>
    internal int NormalizedWrites { get; private set; }

    private void Highlight()
    {
        var highlighter = _highlighter;
        if (highlighter == null) return;
        try { _spans = Copy(highlighter(_value)); }
        catch (Exception e)
        {
            if (_highlighterLogged) return;
            _highlighterLogged = true;
            Plugin.Logger?.LogWarning("Ui: code area highlighter threw (the previous colors stay): " + e.Message);
        }
    }

    private void Render()
    {
        int lineCount = LineCount;
        _errors = UiCodeAreaModel.NormalizeErrors(_rawErrors, lineCount);
        string summary = UiCodeAreaModel.Summary(_errors);
        if (!string.Equals(summary, _summary, StringComparison.Ordinal) && _summaryRow != null)
        {
            _summary = summary;
            try { _summaryRow.SetCell(UiCodeAreaModel.SummaryCellKey, text: summary); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area summary threw: " + e.Message); }
        }

        LastMarkup = UiCodeAreaModel.Markup(_value, _spans);
        _renders++;
        var view = _view;
        if (view == null) return;
        try
        {
            if (!view.Alive)
            {
                _view = null;
                return;
            }
            view.Render(new CodeAreaFrame(LastMarkup, lineCount, _errors));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: code area render threw: " + e.Message); }
    }

    private static IReadOnlyList<UiTextSpan> Copy(IReadOnlyList<UiTextSpan>? spans) =>
        spans == null || spans.Count == 0 ? Array.Empty<UiTextSpan>() : new List<UiTextSpan>(spans);
}
