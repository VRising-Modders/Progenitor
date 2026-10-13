using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// JOINFLOW1: a date and time, stored as ISO 8601 UTC (<c>2026-10-10T18:00:00Z</c>) and shown and edited
/// as a wall-clock time in <see cref="Zone"/> with its offset (<c>Sat, Oct 10, 2026, 6:00 PM (UTC-04:00)</c>).
/// The row is the caption, a value box with the value (or <c>Not set</c>) and a button that opens the
/// picker: a calendar month, hour and minute steppers (and seconds at <see cref="DateTimePickerPrecision.Second"/>),
/// the zone line, and Cancel / Done. Create with <see cref="IUiPanel.DateTimeField"/>.
///
/// <para>Done raises <see cref="OnChanged"/> with the new ISO UTC value when it differs from
/// <see cref="Value"/>; Cancel and Escape change nothing. A wall-clock time that does not exist on its day
/// (clocks moving forward) moves forward by the gap, and an ambiguous one (clocks moving back) takes the
/// first occurrence; the picker says so under the zone line. Disposing the field (or its panel) closes the
/// picker without running anything.</para>
/// </summary>
public interface IUiDateTimeField : IUiHandle
{
    /// <summary>The stored value as ISO 8601 UTC, or empty for none. Setting it does not raise <see cref="OnChanged"/>.</summary>
    string Value { get; set; }

    /// <summary>The zone the value is shown and edited in.</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>Whether the picker edits seconds.</summary>
    DateTimePickerPrecision Precision { get; }

    /// <summary>Called with the new ISO UTC value when Done is pressed and the value changed.</summary>
    IDisposable OnChanged(Action<string> handler);

    /// <summary>Open the picker (the button's click). Does nothing while the field is grayed or disposed.</summary>
    void Open();

    /// <summary>True while this field's picker is on screen.</summary>
    bool IsOpen { get; }
}

/// <summary>
/// JOINFLOW1: the date-time field's and picker's rows as cell rows, game-free so the geometry and the
/// cell keys have tests. The field is one settings-grid row (caption, value box, steel button, reset
/// gutter); the picker is a dialog-width popup of plain rows.
/// </summary>
internal static class UiDateTimeLayout
{
    internal const string CellCaption = "caption";
    internal const string CellValue = "value";
    internal const string CellOpen = "open";
    internal const string CellReset = "reset";
    internal const string CellGutter = "gutter";

    internal const string NotSet = "Not set";
    internal const string SetCaption = "Set";
    internal const string ChangeCaption = "Change";
    internal const string CancelCaption = "Cancel";
    internal const string DoneCaption = "Done";
    internal const string PreviousGlyph = "<";
    internal const string NextGlyph = ">";
    internal const string MinusGlyph = "-";
    internal const string PlusGlyph = "+";
    internal const string TimeCaption = "Time";

    /// <summary>The "who" word of the zone line for a personal value, and the framework's for a server-wide one.</summary>
    internal const string YourTime = "Your time";
    internal const string ServerTime = "Server time";
    internal const string ZoneTime = "Time zone";

    // Picker row keys (the readback addresses).
    internal const string MonthRow = "datetime-month";
    internal const string WeekdayRow = "datetime-weekdays";
    internal const string TimeRow = "datetime-time";
    internal const string PreviewRow = "datetime-preview";
    internal const string ZoneRow = "datetime-zone";
    internal const string NoteRow = "datetime-note";
    internal const string ButtonsRow = "datetime-buttons";

    internal const string CellPrevious = "prev";
    internal const string CellMonth = "month";
    internal const string CellNext = "next";
    internal const string CellCancel = "cancel";
    internal const string CellDone = "done";
    internal const string CellText = "text";

    /// <summary>Row heights (units): month header, weekday header, one week, time, preview, zone, note.</summary>
    internal const float MonthRowHeight = 44f;
    internal const float WeekdayRowHeight = 26f;
    internal const float WeekRowHeight = 38f;
    internal const float TimeRowHeight = 44f;
    internal const float PreviewRowHeight = 28f;
    internal const float ZoneRowHeight = 26f;
    internal const float NoteRowHeight = 44f;

    internal const float ArrowWidth = 56f;
    internal const float StepperWidth = 36f;
    internal const float TimeValueWidth = 48f;
    internal const float ColonWidth = 12f;
    internal const float TimeLabelWidth = 64f;
    internal const float MonthFontSize = 22f;
    internal const float SmallFontSize = 15f;

    /// <summary>The field's button.</summary>
    internal const float OpenButtonWidth = 120f;

    /// <summary>GRIDROW1: the blank lead before the caption (<see cref="CellLayout.GridNameLeadWidth"/>).</summary>
    internal const string CellNameLead = "name-lead";

    /// <summary>
    /// The narrowest the value box gets: what the grid's narrowest control column (<see cref="ModSettingsRowGrid.MinControlWidth"/>)
    /// leaves beside the button (GRIDROW1), so a narrow row shrinks the box, not the caption, and the box stays on the column.
    /// </summary>
    internal const float MinValueWidth = ModSettingsRowGrid.MinControlWidth - OpenButtonWidth - 2f * CellLayout.Gap;

    internal static string WeekRow(int week) => "datetime-week-" + week.ToString(CultureInfo.InvariantCulture);

    internal static string DayKey(int index) => "day-" + index.ToString(CultureInfo.InvariantCulture);

    internal static string WeekdayKey(int index) => "wd-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The time cells of one unit: <c>h</c>, <c>m</c> or <c>s</c> (<c>h-minus</c>, <c>h</c>, <c>h-plus</c>).</summary>
    internal static string MinusKey(char unit) => unit + "-minus";

    internal static string PlusKey(char unit) => unit + "-plus";

    internal static string ValueKey(char unit) => unit.ToString();

    /// <summary>
    /// The reset gutter's width: what the game's Options row keeps between the end of its bars and the
    /// scrollbar band (<see cref="GameSettingsLook.DropdownRightGap"/>), where the reset arrow sits.
    /// </summary>
    internal static float GutterWidth => GameSettingsLook.Kit(GameSettingsLook.DropdownRightGap);

    /// <summary>
    /// The field's row on the settings grid's columns (GRIDROW1, live 2026-10-11: the caption started at the row's
    /// edge, left of every other name): a blank lead to the grid's name inset, the caption over the name column
    /// (<see cref="CellWidth.GridName"/>), the value box from the grid's control column, the button at the bar edge,
    /// then the reset gutter (a reset arrow when <paramref name="resettable"/>, else empty) and the scrollbar band.
    /// </summary>
    internal static CellRowSpec FieldRow(string key, string caption, string value, string button, bool resettable)
    {
        float lead = CellLayout.GridNameLeadWidth;
        var cells = new List<CellSpec>(6)
        {
            new CellSpec(CellKind.Muted, CellNameLead).With(text: "", width: CellWidth.Px(lead), minWidth: lead),
            new CellSpec(CellKind.Text, CellCaption).With(
                text: caption ?? "", width: CellWidth.GridName, fontSize: ModSettingsRowGrid.NameFontSize,
                tone: CellTone.SettingName),
        };
        var box = new CellSpec(CellKind.Input, CellValue).With(
            text: value ?? "", width: CellWidth.Fill(MinValueWidth), minWidth: MinValueWidth, skin: CellSkin.ValueBox);
        box.ReadOnly = true;
        cells.Add(box);
        cells.Add(new CellSpec(CellKind.Button, CellOpen).With(
            text: button ?? "", width: CellWidth.Px(OpenButtonWidth), minWidth: OpenButtonWidth,
            align: CellAlign.Center, skin: CellSkin.KeyCap, fontSize: MenuPanelLayout.HeaderButtonFontSize));
        if (resettable)
            cells.Add(new CellSpec(CellKind.Button, CellReset).With(
                text: ModSettingsView.ResetGlyph, width: CellWidth.Px(GutterWidth), minWidth: GutterWidth,
                align: CellAlign.Center, tone: CellTone.Muted, skin: CellSkin.ResetIcon));
        else
            // GRIDROW1: an empty cell, not a spacer (a spacer measures 0 wide, so the button ran into the gutter).
            cells.Add(new CellSpec(CellKind.Muted, CellGutter).With(text: "", width: CellWidth.Px(GutterWidth), minWidth: GutterWidth));
        return new CellRowSpec(key, cells, height: ModSettingsRowGrid.RowPitch,
            rightInset: HeadBlockLayout.RightColumnInset, plain: true, separator: true);
    }

    /// <summary>One day button's width in a dialog row <paramref name="rowWidth"/> wide.</summary>
    internal static float DayWidth(float rowWidth)
    {
        float w = (float)Math.Floor((MenuPanelLayout.Positive(rowWidth, DialogLayout.RowWidth) - 10f * CellLayout.Gap) / 7f);
        return Math.Max(36f, w);
    }

    /// <summary>The month header: previous, <c>October 2026</c>, next.</summary>
    internal static CellRowSpec MonthRowSpec(string caption)
    {
        var cells = new List<CellSpec>(3)
        {
            new CellSpec(CellKind.Button, CellPrevious).With(
                text: PreviousGlyph, width: CellWidth.Px(ArrowWidth), minWidth: ArrowWidth, align: CellAlign.Center,
                skin: CellSkin.PopupButton, fontSize: DialogLayout.ButtonFontSize),
            new CellSpec(CellKind.Text, CellMonth).With(
                text: caption ?? "", width: CellWidth.Fill(120f), align: CellAlign.Center, fontSize: MonthFontSize),
            new CellSpec(CellKind.Button, CellNext).With(
                text: NextGlyph, width: CellWidth.Px(ArrowWidth), minWidth: ArrowWidth, align: CellAlign.Center,
                skin: CellSkin.PopupButton, fontSize: DialogLayout.ButtonFontSize),
        };
        return new CellRowSpec(MonthRow, cells, height: MonthRowHeight, plain: true);
    }

    /// <summary>The weekday header, Sunday first, muted.</summary>
    internal static CellRowSpec WeekdayRowSpec(float rowWidth)
    {
        float w = DayWidth(rowWidth);
        var cells = new List<CellSpec>(9) { new CellSpec(CellKind.Spacer, "dialog-left") };
        var names = DateTimePickerModel.WeekdayCaptions;
        for (int i = 0; i < names.Count; i++)
            cells.Add(new CellSpec(CellKind.Muted, WeekdayKey(i)).With(
                text: names[i], width: CellWidth.Px(w), minWidth: w, align: CellAlign.Center, fontSize: SmallFontSize));
        cells.Add(new CellSpec(CellKind.Spacer, "dialog-right"));
        return new CellRowSpec(WeekdayRow, cells, height: WeekdayRowHeight, plain: true);
    }

    /// <summary>Week <paramref name="week"/> (0-5) of the grid: seven day buttons, cells <c>day-(7w)</c>..<c>day-(7w+6)</c>.</summary>
    internal static CellRowSpec WeekRowSpec(int week, IReadOnlyList<DateTimePickerDay> grid, float rowWidth)
    {
        float w = DayWidth(rowWidth);
        var cells = new List<CellSpec>(9) { new CellSpec(CellKind.Spacer, "dialog-left") };
        for (int d = 0; d < 7; d++)
        {
            int index = week * 7 + d;
            var day = grid != null && index < grid.Count ? grid[index] : default;
            cells.Add(new CellSpec(CellKind.Button, DayKey(index)).With(
                text: DayText(day), width: CellWidth.Px(w), minWidth: w, align: CellAlign.Center,
                tone: DayTone(day), skin: CellSkin.PopupButton, fontSize: DialogLayout.ButtonFontSize));
        }

        cells.Add(new CellSpec(CellKind.Spacer, "dialog-right"));
        return new CellRowSpec(WeekRow(week), cells, height: WeekRowHeight, plain: true);
    }

    /// <summary>The time row: <c>Time</c>, then − HH + : − MM + (and : − SS + with seconds), centered.</summary>
    internal static CellRowSpec TimeRowSpec(bool seconds)
    {
        var cells = new List<CellSpec>(16)
        {
            new CellSpec(CellKind.Spacer, "dialog-left"),
            new CellSpec(CellKind.Muted, "time-label").With(
                text: TimeCaption, width: CellWidth.Px(TimeLabelWidth), align: CellAlign.Right, fontSize: SmallFontSize),
        };
        AddUnit(cells, 'h');
        cells.Add(Colon("colon-1"));
        AddUnit(cells, 'm');
        if (seconds)
        {
            cells.Add(Colon("colon-2"));
            AddUnit(cells, 's');
        }

        cells.Add(new CellSpec(CellKind.Spacer, "dialog-right"));
        return new CellRowSpec(TimeRow, cells, height: TimeRowHeight, plain: true);
    }

    private static void AddUnit(List<CellSpec> cells, char unit)
    {
        cells.Add(new CellSpec(CellKind.Button, MinusKey(unit)).With(
            text: MinusGlyph, width: CellWidth.Px(StepperWidth), minWidth: StepperWidth, align: CellAlign.Center,
            skin: CellSkin.PopupButton, fontSize: DialogLayout.ButtonFontSize));
        cells.Add(new CellSpec(CellKind.Text, ValueKey(unit)).With(
            text: "00", width: CellWidth.Px(TimeValueWidth), minWidth: TimeValueWidth, align: CellAlign.Center,
            fontSize: MonthFontSize));
        cells.Add(new CellSpec(CellKind.Button, PlusKey(unit)).With(
            text: PlusGlyph, width: CellWidth.Px(StepperWidth), minWidth: StepperWidth, align: CellAlign.Center,
            skin: CellSkin.PopupButton, fontSize: DialogLayout.ButtonFontSize));
    }

    private static CellSpec Colon(string key) =>
        new CellSpec(CellKind.Text, key).With(text: ":", width: CellWidth.Px(ColonWidth), align: CellAlign.Center, fontSize: MonthFontSize);

    /// <summary>A single centered line (the preview, the zone line, the note).</summary>
    internal static CellRowSpec LineSpec(string rowKey, string text, CellKind kind, CellTone tone, float height, bool wrap)
    {
        var cell = new CellSpec(kind, CellText).With(
            text: text ?? "", align: CellAlign.Center, tone: tone, fontSize: SmallFontSize,
            wrap: wrap ? CellWrap.Wrap : CellWrap.Ellipsis, vAlign: wrap ? CellVAlign.Top : CellVAlign.Middle);
        return DialogLayout.CentredRowSpec(rowKey, cell, DialogLayout.BodyWidth(DialogLayout.ContentRowWidth()), height);
    }

    /// <summary>Cancel and Done, the dialog's centered button row (Done gold, rightmost).</summary>
    internal static CellRowSpec ButtonRowSpec() =>
        DialogLayout.ButtonRowSpec(ButtonsRow, new[]
        {
            new DialogButton(CellCancel, CancelCaption, DialogButtonRole.Cancel),
            new DialogButton(CellDone, DoneCaption, DialogButtonRole.Main, CellTone.Gold),
        }, DialogLayout.ContentRowWidth());

    /// <summary>A day button's caption: the day of the month.</summary>
    internal static string DayText(DateTimePickerDay day) =>
        day.Date == default ? "" : day.Date.Day.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A day button's tone: the selected day gold, today pale blue, a neighboring month's day faint,
    /// every other day the normal text.
    /// </summary>
    internal static CellTone DayTone(DateTimePickerDay day) =>
        day.Selected ? CellTone.Gold
        : day.Today ? CellTone.Info
        : !day.InMonth ? CellTone.Faint
        : CellTone.Normal;

    /// <summary>A two-digit time value.</summary>
    internal static string TwoDigits(int value) => value.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>What the field's value box shows: the wall-clock value and its offset, <c>Not set</c>, or an unreadable value as it is.</summary>
    internal static string DisplayValue(string? isoUtc, TimeZoneInfo? zone, DateTimePickerPrecision precision)
    {
        if (string.IsNullOrWhiteSpace(isoUtc)) return NotSet;
        string shown = DateTimePickerModel.DisplayStored(isoUtc, zone, precision);
        return shown.Length > 0 ? shown : isoUtc!.Trim() + " (not a date)";
    }

    /// <summary>The stored form of a value a caller set: an ISO UTC value normalized, empty kept empty, anything else kept as it is.</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return ResetCountdown.TryParseUtc(value, out var utc) ? ResetCountdown.FormatUtc(utc) : value!.Trim();
    }
}

/// <summary>
/// JOINFLOW1: the handle behind <see cref="IUiDateTimeField"/>, composed from existing primitives: one
/// cell row on the caller's panel (caption, value box, button) and, while open, a
/// <see cref="UiDateTimePicker"/> popup. It is a child of its panel (<see cref="UiPanelHandle.AdoptChild"/>),
/// so a page rebuild, the panel closing and the owner's unload dispose it, and with it the popup. Every
/// callback is held here, in Core.
/// </summary>
internal sealed class UiDateTimeField : UiHandle, IUiDateTimeField
{
    private static int _rows;
    private readonly IUiCellRow _row;
    private readonly UiChangeHandlers<string> _changed = new();
    private readonly TimeZoneInfo? _zone;
    private readonly DateTimePickerPrecision _precision;
    private readonly bool _resettable;
    private string _value;
    private UiDateTimePicker? _picker;
    private Action? _reset;

    /// <summary>The field whose picker is on screen (one at a time), or null.</summary>
    internal static UiDateTimeField? Current { get; private set; }

    /// <summary>The picker's panel: a test replaces it; null uses <see cref="Ui.Panel"/>.</summary>
    internal static Func<string, object?, string, IUiPanel>? PanelFactory { get; set; }

    private UiDateTimeField(
        IUiPanel panel, object? owner, IUiCellRow row, string caption, string value, TimeZoneInfo? zone,
        DateTimePickerPrecision precision, bool resettable)
        : base(panel?.PluginId ?? UiOwnership.UnownedId, panel?.RootName ?? UiOwnership.RootName(panel?.PluginId ?? UiOwnership.UnownedId),
            owner, (row as UiHandle)?.Runtime, row?.Enabled ?? false)
    {
        _row = row!;
        Caption = caption ?? "";
        _value = value;
        _zone = zone;
        _precision = precision;
        _resettable = resettable;
        Who = zone == null ? UiDateTimeLayout.YourTime : UiDateTimeLayout.ZoneTime;
        _row.OnClick(UiDateTimeLayout.CellOpen, Open);
        if (resettable) _row.OnClick(UiDateTimeLayout.CellReset, () => { if (Interactable) _reset?.Invoke(); });
    }

    /// <summary>The public factory (<see cref="IUiPanel.DateTimeField"/>).</summary>
    internal static UiDateTimeField Create(
        IUiPanel panel, string text, string? isoUtc, TimeZoneInfo? zone, DateTimePickerPrecision precision) =>
        Create(panel, text, isoUtc, zone, precision, resettable: false);

    /// <summary>The settings page's factory: <paramref name="resettable"/> puts the reset arrow in the row's gutter.</summary>
    internal static UiDateTimeField Create(
        IUiPanel panel, string text, string? isoUtc, TimeZoneInfo? zone, DateTimePickerPrecision precision, bool resettable)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        string value = UiDateTimeLayout.Normalize(isoUtc);
        string caption = text ?? "";
        string key = "datetime:" + caption + "#" + (++_rows).ToString(CultureInfo.InvariantCulture);
        var spec = UiDateTimeLayout.FieldRow(
            key, caption, UiDateTimeLayout.DisplayValue(value, zone ?? TimeZoneInfo.Local, precision),
            value.Length == 0 ? UiDateTimeLayout.SetCaption : UiDateTimeLayout.ChangeCaption, resettable);
        var row = panel.CellRow(spec);
        var host = panel as UiPanelHandle;
        var field = new UiDateTimeField(panel, host?.Owner, row, caption, value, zone, precision, resettable);
        host?.AdoptChild(field);
        return field;
    }

    /// <summary>The caption (the row's name and the picker's title).</summary>
    internal string Caption { get; }

    /// <summary>The cell row this field draws.</summary>
    internal IUiCellRow Row => _row;

    /// <summary>The zone line's "who" word: <c>Your time</c>, <c>Server time</c>, or <c>Time zone</c> for a caller's zone.</summary>
    internal string Who { get; set; }

    /// <summary>The settings page's zone function, read when the field is drawn and when the picker opens.</summary>
    internal Func<TimeZoneInfo?>? ZoneSource { get; set; }

    /// <summary>The settings page's seconds function, read when the picker opens (true: seconds).</summary>
    internal Func<bool>? SecondsSource { get; set; }

    /// <summary>The picker on screen, or null.</summary>
    internal UiDateTimePicker? Picker => _picker is { IsOpen: true } open ? open : null;

    public TimeZoneInfo Zone
    {
        get
        {
            var source = ZoneSource;
            if (source != null)
            {
                try
                {
                    var z = source();
                    if (z != null) return z;
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: date-time zone function threw: " + e.Message); }
            }

            return _zone ?? TimeZoneInfo.Local;
        }
    }

    public DateTimePickerPrecision Precision
    {
        get
        {
            var source = SecondsSource;
            if (source == null) return _precision;
            try { return source() ? DateTimePickerPrecision.Second : DateTimePickerPrecision.Minute; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: date-time seconds function threw: " + e.Message);
                return _precision;
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            _value = UiDateTimeLayout.Normalize(value);
            Redraw();
        }
    }

    /// <summary>What the value box shows now.</summary>
    internal string DisplayText => UiDateTimeLayout.DisplayValue(_value, Zone, Precision);

    public bool IsOpen => Picker != null;

    public IDisposable OnChanged(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (IsDisposed) return Noop.Instance;
        return _changed.Add(handler);
    }

    /// <summary>The settings page's reset: the gutter arrow's click (a row built resettable only).</summary>
    internal void SetReset(Action? onReset) => _reset = onReset;

    /// <summary>The reset arrow's state, as the settings page draws it: muted at the default, normal when changed, hidden when none.</summary>
    internal void PaintReset(SettingsResetState state)
    {
        ResetState = state;
        if (!_resettable || IsDisposed) return;
        _row.SetCell(UiDateTimeLayout.CellReset,
            tone: state == SettingsResetState.Changed ? CellTone.Info : CellTone.Muted,
            visible: state != SettingsResetState.None);
    }

    internal SettingsResetState ResetState { get; private set; } = SettingsResetState.None;

    /// <summary>Rewrite the value box and the button caption from <see cref="Value"/> (and the zone function).</summary>
    internal void Redraw()
    {
        if (IsDisposed) return;
        _row.SetCell(UiDateTimeLayout.CellValue, text: DisplayText);
        _row.SetCell(UiDateTimeLayout.CellOpen,
            text: _value.Length == 0 ? UiDateTimeLayout.SetCaption : UiDateTimeLayout.ChangeCaption);
    }

    /// <summary>The button's click: open the picker on <see cref="Value"/> in <see cref="Zone"/>. One picker at a time.</summary>
    public void Open()
    {
        if (IsDisposed)
        {
            Plugin.Logger?.LogInfo("Ui: date-time field '" + Caption + "' is disposed; picker not opened.");
            return;
        }

        if (!Interactable)
        {
            Plugin.Logger?.LogInfo("Ui: date-time field '" + Caption + "' is grayed; picker not opened.");
            return;
        }

        var current = Current;
        if (current != null && !ReferenceEquals(current, this)) current.Cancel();
        ClosePicker();
        var zone = Zone;
        var precision = Precision;
        Redraw();
        var model = new DateTimePickerModel(_value, zone, precision);
        var factory = PanelFactory ?? ((title, owner, plugin) => Ui.Panel(title, owner, plugin));
        try
        {
            _picker = UiDateTimePicker.Open(this, model, factory);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: date-time picker open threw: " + e.Message);
            _picker = null;
            return;
        }

        Current = this;
        Plugin.Logger?.LogInfo("Ui: date-time picker opened for '" + Caption + "' (" + model.IsoUtc + ", "
            + model.ZoneLabel(Who) + ").");
    }

    /// <summary>The picker's Done: close it and, when the value changed, store it and raise <see cref="OnChanged"/>.</summary>
    internal void Done()
    {
        var picker = Picker;
        if (picker == null) return;
        string iso = picker.Model.IsoUtc;
        ClosePicker();
        if (IsDisposed || string.Equals(iso, _value, StringComparison.Ordinal))
        {
            Plugin.Logger?.LogInfo("Ui: date-time picker '" + Caption + "' done; unchanged.");
            return;
        }

        _value = iso;
        Redraw();
        Plugin.Logger?.LogInfo("Ui: date-time picker '" + Caption + "' done: " + iso + ".");
        _changed.Raise(iso);
    }

    /// <summary>The picker's Cancel (and Escape): close it, change nothing.</summary>
    internal void Cancel()
    {
        if (_picker == null) return;
        ClosePicker();
        Plugin.Logger?.LogInfo("Ui: date-time picker '" + Caption + "' canceled.");
    }

    private void ClosePicker()
    {
        var picker = _picker;
        _picker = null;
        if (ReferenceEquals(Current, this)) Current = null;
        try { picker?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: date-time picker close threw: " + e.Message); }
    }

    protected override void OnDispose()
    {
        // The picker goes with the field, running nothing; the row is the panel's and is released with
        // it, or here when the field is disposed on its own.
        ClosePicker();
        _changed.Clear();
        _reset = null;
        ZoneSource = null;
        SecondsSource = null;
        try { _row.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: date-time row dispose threw: " + e.Message); }
    }
}

/// <summary>
/// JOINFLOW1: the date-time picker's popup — its own panel with the dialog chrome
/// (<see cref="Ui.DialogChrome"/> through <see cref="UiDialogView.Build"/>), titled with the field's
/// caption: the month header, the weekday header, six weeks of day buttons, the time steppers, the
/// selected time, the zone line, the daylight-saving note and Cancel / Done. Every change rewrites cells
/// in place (<see cref="IUiCellRow.SetCell"/>); nothing is rebuilt while it is open. Escape is Cancel
/// (<see cref="Ui.PushEscape"/>), Enter is Done after the dialog's grace. Disposing it runs nothing.
/// </summary>
internal sealed class UiDateTimePicker : IDisposable
{
    private readonly UiDateTimeField _field;
    private readonly IUiPanel _panel;
    private readonly List<IUiCellRow> _weeks = new(6);
    private readonly DateTime[] _days = new DateTime[42];
    private IUiCellRow? _month;
    private IUiCellRow? _time;
    private IUiCellRow? _preview;
    private IUiCellRow? _zone;
    private IUiCellRow? _note;
    private IUiCellRow? _buttons;
    private IDisposable? _escape;
    private Func<bool>? _enter;
    private long _openedAt;
    private bool _disposed;

    private UiDateTimePicker(UiDateTimeField field, DateTimePickerModel model, IUiPanel panel)
    {
        _field = field;
        Model = model;
        _panel = panel;
    }

    internal DateTimePickerModel Model { get; }

    internal IUiPanel Panel => _panel;

    /// <summary>The picker's title: the field's caption.</summary>
    internal string Title => _field.Caption;

    internal bool IsOpen => !_disposed && !_panel.IsDisposed;

    internal static UiDateTimePicker Open(UiDateTimeField field, DateTimePickerModel model, Func<string, object?, string, IUiPanel> factory)
    {
        var panel = factory(field.Caption, field.Owner, field.PluginId);
        var picker = new UiDateTimePicker(field, model, panel);
        try
        {
            picker.Build();
        }
        catch
        {
            picker.Dispose();
            throw;
        }

        try { Ui.Show(panel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: date-time picker show threw: " + e.Message); }
        picker._escape = Ui.PushEscape(picker.Escape);
        try { picker._openedAt = UiDialog.Clock(); } catch { picker._openedAt = 0; }
        picker._enter = picker.Enter;
        UiDialog.EnterFallback = picker._enter;
        return picker;
    }

    private void Build()
    {
        string footer = "";
        try { footer = Ui.FrameworkFooter; } catch { /* no versions in this process */ }
        UiDialogView.Build(_panel, new DialogContent { Shape = UiDialogShape.Notice, Title = _field.Caption, Footer = footer });
        float rowWidth = DialogLayout.ContentRowWidth();

        _month = _panel.CellRow(UiDateTimeLayout.MonthRowSpec(Model.MonthCaption));
        _month.OnClick(UiDateTimeLayout.CellPrevious, PreviousMonth);
        _month.OnClick(UiDateTimeLayout.CellNext, NextMonth);

        _panel.CellRow(UiDateTimeLayout.WeekdayRowSpec(rowWidth));
        var grid = Model.Grid();
        for (int w = 0; w < 6; w++)
        {
            var row = _panel.CellRow(UiDateTimeLayout.WeekRowSpec(w, grid, rowWidth));
            for (int d = 0; d < 7; d++)
            {
                int index = w * 7 + d;
                row.OnClick(UiDateTimeLayout.DayKey(index), () => ClickDay(index));
            }

            _weeks.Add(row);
        }

        bool seconds = Model.Precision == DateTimePickerPrecision.Second;
        _time = _panel.CellRow(UiDateTimeLayout.TimeRowSpec(seconds));
        _time.OnClick(UiDateTimeLayout.MinusKey('h'), () => StepHour(-1));
        _time.OnClick(UiDateTimeLayout.PlusKey('h'), () => StepHour(+1));
        _time.OnClick(UiDateTimeLayout.MinusKey('m'), () => StepMinute(-1));
        _time.OnClick(UiDateTimeLayout.PlusKey('m'), () => StepMinute(+1));
        if (seconds)
        {
            _time.OnClick(UiDateTimeLayout.MinusKey('s'), () => StepSecond(-1));
            _time.OnClick(UiDateTimeLayout.PlusKey('s'), () => StepSecond(+1));
        }

        _preview = _panel.CellRow(UiDateTimeLayout.LineSpec(
            UiDateTimeLayout.PreviewRow, Model.DisplayText, CellKind.Text, CellTone.Normal, UiDateTimeLayout.PreviewRowHeight, wrap: false));
        _zone = _panel.CellRow(UiDateTimeLayout.LineSpec(
            UiDateTimeLayout.ZoneRow, Model.ZoneLabel(_field.Who), CellKind.Muted, CellTone.Muted, UiDateTimeLayout.ZoneRowHeight, wrap: false));
        _note = _panel.CellRow(UiDateTimeLayout.LineSpec(
            UiDateTimeLayout.NoteRow, "", CellKind.Text, CellTone.Orange, UiDateTimeLayout.NoteRowHeight, wrap: true));
        _buttons = _panel.CellRow(UiDateTimeLayout.ButtonRowSpec());
        _buttons.OnClick(UiDateTimeLayout.CellCancel, _field.Cancel);
        _buttons.OnClick(UiDateTimeLayout.CellDone, _field.Done);
        Refresh();
    }

    // ---- the buttons' handlers (the bridge calls the same ones) ------------------------------------

    internal void PreviousMonth()
    {
        if (!IsOpen) return;
        Model.PreviousMonth();
        Refresh();
    }

    internal void NextMonth()
    {
        if (!IsOpen) return;
        Model.NextMonth();
        Refresh();
    }

    /// <summary>Show a month directly (the bridge's <c>{year, month}</c>; no button does this).</summary>
    internal void ShowMonth(int year, int month)
    {
        if (!IsOpen) return;
        Model.ShowMonth(year, month);
        Refresh();
    }

    /// <summary>A click on grid cell <paramref name="index"/> (0-41): pick that date, keeping the time.</summary>
    internal void ClickDay(int index)
    {
        if (!IsOpen || index < 0 || index >= _days.Length) return;
        Model.PickDay(_days[index]);
        Refresh();
    }

    internal void StepHour(int delta)
    {
        if (!IsOpen) return;
        Model.StepHour(delta);
        Refresh();
    }

    internal void StepMinute(int delta)
    {
        if (!IsOpen) return;
        Model.StepMinute(delta);
        Refresh();
    }

    internal void StepSecond(int delta)
    {
        if (!IsOpen) return;
        Model.StepSecond(delta);
        Refresh();
    }

    /// <summary>The grid index of day <paramref name="day"/> of the shown month, or -1.</summary>
    internal int IndexOfDay(int day)
    {
        for (int i = 0; i < _days.Length; i++)
            if (_days[i].Month == Model.ViewMonth && _days[i].Year == Model.ViewYear && _days[i].Day == day) return i;
        return -1;
    }

    private bool Escape()
    {
        if (!IsOpen) return false;
        _field.Cancel();
        return true;
    }

    private bool Enter()
    {
        if (!IsOpen) return false;
        long now;
        try { now = UiDialog.Clock(); } catch { return false; }
        if (now - _openedAt < UiDialog.EnterGraceMs) return false;
        _field.Done();
        return true;
    }

    /// <summary>Rewrite every cell from the model, in place.</summary>
    internal void Refresh()
    {
        if (!IsOpen) return;
        _month?.SetCell(UiDateTimeLayout.CellMonth, text: Model.MonthCaption);
        var grid = Model.Grid();
        for (int i = 0; i < grid.Count && i < _days.Length; i++)
        {
            _days[i] = grid[i].Date;
            var row = i / 7 < _weeks.Count ? _weeks[i / 7] : null;
            row?.SetCell(UiDateTimeLayout.DayKey(i), text: UiDateTimeLayout.DayText(grid[i]), tone: UiDateTimeLayout.DayTone(grid[i]));
        }

        _time?.SetCell(UiDateTimeLayout.ValueKey('h'), text: UiDateTimeLayout.TwoDigits(Model.Hour));
        _time?.SetCell(UiDateTimeLayout.ValueKey('m'), text: UiDateTimeLayout.TwoDigits(Model.Minute));
        if (Model.Precision == DateTimePickerPrecision.Second)
            _time?.SetCell(UiDateTimeLayout.ValueKey('s'), text: UiDateTimeLayout.TwoDigits(Model.Second));
        _preview?.SetCell(UiDateTimeLayout.CellText, text: Model.DisplayText);
        _zone?.SetCell(UiDateTimeLayout.CellText, text: Model.ZoneLabel(_field.Who));
        string note = Model.Note;
        _note?.SetCell(UiDateTimeLayout.CellText, text: note);
        if (_note != null && _note.Visible != note.Length > 0) _note.Visible = note.Length > 0;
    }

    /// <summary>The <c>datetime_read</c> readback.</summary>
    internal Dictionary<string, object?> Describe()
    {
        var grid = Model.Grid();
        var cells = new List<object?>(grid.Count);
        for (int i = 0; i < grid.Count; i++)
        {
            cells.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["date"] = grid[i].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["inMonth"] = grid[i].InMonth,
                ["selected"] = grid[i].Selected,
                ["today"] = grid[i].Today,
            });
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["open"] = IsOpen,
            ["title"] = Title,
            ["viewYear"] = Model.ViewYear,
            ["viewMonth"] = Model.ViewMonth,
            ["caption"] = Model.MonthCaption,
            ["selectedLocal"] = Model.Local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["display"] = Model.DisplayText,
            ["iso"] = Model.IsoUtc,
            ["zone"] = Model.ZoneLabel(_field.Who),
            ["note"] = Model.Note,
            ["noteShown"] = _note?.Visible ?? false,
            ["precision"] = Model.Precision == DateTimePickerPrecision.Second ? "second" : "minute",
            ["hour"] = Model.Hour,
            ["minute"] = Model.Minute,
            ["second"] = Model.Second,
            ["fieldValue"] = _field.Value,
            ["cells"] = cells,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var escape = _escape;
        _escape = null;
        try { escape?.Dispose(); } catch { /* released once */ }
        if (_enter != null && ReferenceEquals(UiDialog.EnterFallback, _enter)) UiDialog.EnterFallback = null;
        _enter = null;
        try { _panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: date-time picker dispose threw: " + e.Message); }
        _weeks.Clear();
        _month = _time = _preview = _zone = _note = _buttons = null;
    }
}

/// <summary>
/// JOINFLOW1: the dev bridge's <c>mods_ui datetime_*</c> actions, reached by reflection from
/// <c>Progenitor.Dev</c>. Each one calls the handler the matching button calls (the month arrows, a day
/// button, the time steppers, Cancel, Done). Main thread, client only.
/// </summary>
internal static class UiDateTimeBridge
{
    private static Dictionary<string, object?> Fail(string error) =>
        new(StringComparer.Ordinal) { ["ok"] = false, ["error"] = error };

    private static UiDateTimePicker? Picker => UiDateTimeField.Current?.Picker;

    /// <summary><c>datetime_open {label}</c>: press the button of the drawn settings page's date-time row captioned <paramref name="label"/>.</summary>
    public static Dictionary<string, object?> Open(string? label)
    {
        var view = ModSettingsView.Drawn;
        if (view == null) return Fail("no mod settings page is drawn (open a mod's page in the Mods workspace)");
        var field = view.DateTimeFieldForBridge(label);
        if (field == null) return Fail("no date-time row captioned '" + label + "' on the drawn page");
        if (!field.Interactable) return Fail("the row '" + label + "' is grayed: " + view.WhyForBridge(label));
        field.Open();
        var d = Read();
        d["handler"] = "the row's Change button (UiDateTimeField.Open)";
        return d;
    }

    /// <summary><c>datetime_read</c>: the open picker's state.</summary>
    public static Dictionary<string, object?> Read()
    {
        var picker = Picker;
        if (picker == null) return new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = true, ["open"] = false };
        return picker.Describe();
    }

    /// <summary><c>datetime_month {delta}</c> (the arrows, once per step) or <c>{year, month}</c>.</summary>
    public static Dictionary<string, object?> Month(int? delta, int? year, int? month)
    {
        var picker = Picker;
        if (picker == null) return Fail("no date-time picker is open");
        if (year != null || month != null)
        {
            if (year == null || month == null || month < 1 || month > 12) return Fail("give year and month (1-12) together");
            picker.ShowMonth(year.Value, month.Value);
        }
        else
        {
            int n = delta ?? 0;
            if (n == 0 || Math.Abs(n) > 1200) return Fail("delta must be a non-zero number of months (at most 1200)");
            for (int i = 0; i < Math.Abs(n); i++)
            {
                if (n < 0) picker.PreviousMonth();
                else picker.NextMonth();
            }
        }

        return Read();
    }

    /// <summary><c>datetime_day {day}</c>: click day <paramref name="day"/> of the shown month.</summary>
    public static Dictionary<string, object?> Day(int day)
    {
        var picker = Picker;
        if (picker == null) return Fail("no date-time picker is open");
        int index = picker.IndexOfDay(day);
        if (index < 0) return Fail("the shown month has no day " + day.ToString(CultureInfo.InvariantCulture));
        picker.ClickDay(index);
        var d = Read();
        d["clickedCell"] = index;
        return d;
    }

    /// <summary>
    /// <c>datetime_time {hour, minute, second}</c> (any subset): press the steppers until each unit reads
    /// the asked value (the shorter way round).
    /// </summary>
    public static Dictionary<string, object?> Time(int? hour, int? minute, int? second)
    {
        var picker = Picker;
        if (picker == null) return Fail("no date-time picker is open");
        if (hour is < 0 or > 23) return Fail("hour must be 0-23");
        if (minute is < 0 or > 59) return Fail("minute must be 0-59");
        if (second is < 0 or > 59) return Fail("second must be 0-59");
        if (second != null && picker.Model.Precision != DateTimePickerPrecision.Second)
            return Fail("this picker edits minutes only (no seconds)");
        int presses = 0;
        if (hour != null) presses += Press(picker.Model.Hour, hour.Value, 24, picker.StepHour);
        if (minute != null) presses += Press(picker.Model.Minute, minute.Value, 60, picker.StepMinute);
        if (second != null) presses += Press(picker.Model.Second, second.Value, 60, picker.StepSecond);
        var d = Read();
        d["presses"] = presses;
        return d;
    }

    private static int Press(int current, int target, int modulus, Action<int> step)
    {
        int forward = ((target - current) % modulus + modulus) % modulus;
        int backward = modulus - forward;
        int sign = forward <= backward ? +1 : -1;
        int count = forward <= backward ? forward : backward;
        for (int i = 0; i < count; i++) step(sign);
        return count;
    }

    /// <summary><c>datetime_done</c>: press Done. Reports the field's value after.</summary>
    public static Dictionary<string, object?> Done()
    {
        var field = UiDateTimeField.Current;
        if (field?.Picker == null) return Fail("no date-time picker is open");
        string iso = field.Picker.Model.IsoUtc;
        field.Done();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["open"] = field.IsOpen,
            ["iso"] = iso,
            ["fieldValue"] = field.Value,
            ["display"] = field.DisplayText,
            ["handler"] = "the picker's Done button (UiDateTimeField.Done)",
        };
    }

    /// <summary><c>datetime_cancel</c>: press Cancel.</summary>
    public static Dictionary<string, object?> Cancel()
    {
        var field = UiDateTimeField.Current;
        if (field?.Picker == null) return Fail("no date-time picker is open");
        field.Cancel();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["open"] = field.IsOpen,
            ["fieldValue"] = field.Value,
            ["handler"] = "the picker's Cancel button (UiDateTimeField.Cancel)",
        };
    }
}
