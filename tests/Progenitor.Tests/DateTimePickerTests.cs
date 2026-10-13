using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW1: the date-time picker — the game-free model (month grid, leap years, navigation, steppers,
/// precision, ISO UTC round trip, daylight-saving gaps and overlaps), the settings builder's
/// <c>DateTime(...)</c> row, the composed field and its popup, the settings page's personal and
/// administrator date-time rows, the administrator's choice and number controls, and the dev bridge's
/// handlers. Game-free: the fake UI runtime and the ConfigFile shim; nothing here is live.
/// </summary>
[Collection("PluginLogger")]
public sealed class DateTimePickerTests : IDisposable
{
    private const string Id = "example.datetime.page";
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly List<IDisposable> _handles = new();
    private JsonDocument? _rows;
    private SettingsScopeContext _context = new();
    private byte[]? _sent;

    public DateTimePickerTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(e => ConfigSync.IsRegistered(Id, e));
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestSend = bytes => { _sent = bytes; return true; };
        UiInfoHandle.Scheduler = null;
        UiSliderEntryHandle.SettleScheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ScopeContextForTests = _ => _context;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-datetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var h in _handles) h.Dispose();
        ModsMenu.ScopeContextForTests = null;
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        UiInfoHandle.Scheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ServerSettings.Uninstall();
        ConfigSync.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- zones -----------------------------------------------------------------------------------

    /// <summary>A US-Eastern-like zone: UTC-5, daylight UTC-4 from the second Sunday of March 02:00 to the first Sunday of November 02:00.</summary>
    private static TimeZoneInfo Eastern()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Standard", "Test Daylight", new[] { rule });
    }

    private static TimeZoneInfo PlusTwo() =>
        TimeZoneInfo.CreateCustomTimeZone("Test Plus2", TimeSpan.FromHours(2), "Test Plus2", "Test Plus2");

    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    // ---- the model -------------------------------------------------------------------------------

    /// <summary>
    /// The month grid is six Sunday-first weeks with the neighbors' days marked out of month and the
    /// selected and today cells marked once each. Catches a Monday-first grid, a 35-cell month, or a
    /// selection drawn on two cells.
    /// </summary>
    [Fact]
    public void The_month_grid_is_42_sunday_first_cells_with_marks()
    {
        var model = new DateTimePickerModel("2026-10-10T18:00:00Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Minute, Now);
        var grid = model.Grid();
        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateTime(2026, 9, 27), grid[0].Date);
        Assert.Equal(DayOfWeek.Sunday, grid[0].Date.DayOfWeek);
        Assert.False(grid[0].InMonth);
        Assert.Equal(new DateTime(2026, 10, 1), grid[4].Date);
        Assert.True(grid[4].InMonth);
        Assert.Equal(new DateTime(2026, 11, 7), grid[41].Date);
        Assert.False(grid[41].InMonth);
        Assert.Equal(31, grid.Count(c => c.InMonth));
        Assert.Equal(new DateTime(2026, 10, 10), grid.Single(c => c.Selected).Date);
        Assert.Equal(new DateTime(2026, 10, 4), grid.Single(c => c.Today).Date);
        Assert.Equal("October 2026", model.MonthCaption);
        Assert.Equal(new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" }, DateTimePickerModel.WeekdayCaptions);
    }

    [Theory]
    [InlineData(2028, true)]
    [InlineData(2100, false)]
    [InlineData(2000, true)]
    [InlineData(2027, false)]
    public void February_29_exists_only_in_leap_years(int year, bool leap)
    {
        var model = new DateTimePickerModel("2026-10-10T18:00:00Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Minute, Now);
        model.ShowMonth(year, 2);
        Assert.Equal(leap ? 29 : 28, DateTimePickerModel.Grid(year, 2).Count(c => c.InMonth));
        Assert.Equal(leap, model.PickDayOfMonth(29));
        if (leap) Assert.Equal(new DateTime(year, 2, 29, 18, 0, 0), model.Local);
    }

    [Fact]
    public void Previous_and_next_month_cross_years_and_move_only_the_view()
    {
        var model = new DateTimePickerModel("2027-01-15T08:00:00Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Minute, Now);
        model.PreviousMonth();
        Assert.Equal((2026, 12), (model.ViewYear, model.ViewMonth));
        model.NextMonth();
        model.NextMonth();
        Assert.Equal((2027, 2), (model.ViewYear, model.ViewMonth));
        Assert.Equal("2027-01-15T08:00:00Z", model.IsoUtc);
    }

    [Fact]
    public void Picking_a_day_keeps_the_time_and_a_neighbor_day_moves_the_view()
    {
        var model = new DateTimePickerModel("2026-10-10T18:30:00Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Minute, Now);
        var november = model.Grid().First(c => !c.InMonth && c.Date.Month == 11);
        model.PickDay(november.Date);
        Assert.Equal(new DateTime(2026, 11, 1, 18, 30, 0), model.Local);
        Assert.Equal((2026, 11), (model.ViewYear, model.ViewMonth));
        Assert.Equal("2026-11-01T18:30:00Z", model.IsoUtc);
    }

    [Fact]
    public void Steppers_wrap_without_changing_the_date()
    {
        var model = new DateTimePickerModel("2026-10-10T23:00:59Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Second, Now);
        model.StepHour(+1);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 59), model.Local);
        model.StepMinute(-1);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 59, 59), model.Local);
        model.StepSecond(+1);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 59, 0), model.Local);
        model.StepHour(-1);
        Assert.Equal(new DateTime(2026, 10, 10, 23, 59, 0), model.Local);
    }

    [Fact]
    public void Minute_precision_drops_seconds_and_ignores_the_seconds_stepper()
    {
        var minute = new DateTimePickerModel("2026-10-10T18:30:45Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Minute, Now);
        Assert.Equal(0, minute.Second);
        Assert.Equal("2026-10-10T18:30:00Z", minute.IsoUtc);
        minute.StepSecond(+5);
        minute.SetSecond(20);
        Assert.Equal(0, minute.Second);
        Assert.Equal("Sat, Oct 10, 2026, 6:30 PM", minute.DisplayText);

        var second = new DateTimePickerModel("2026-10-10T18:30:45Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Second, Now);
        Assert.Equal("2026-10-10T18:30:45Z", second.IsoUtc);
        Assert.Equal("Sat, Oct 10, 2026, 6:30:45 PM", second.DisplayText);
    }

    [Theory]
    [InlineData("2026-10-10T18:00:00Z")]
    [InlineData("2027-03-14T07:30:00Z")]
    [InlineData("2028-02-29T23:59:00Z")]
    public void The_iso_utc_value_round_trips(string iso)
    {
        foreach (var zone in new[] { TimeZoneInfo.Utc, PlusTwo(), Eastern() })
            Assert.Equal(iso, new DateTimePickerModel(iso, zone, DateTimePickerPrecision.Minute, Now).IsoUtc);
        Assert.Equal(iso, UiDateTimeLayout.Normalize(" " + iso + " "));
        Assert.Equal("2026-10-10T16:00:00Z", UiDateTimeLayout.Normalize("2026-10-10T18:00:00+02:00"));
        Assert.Equal("", UiDateTimeLayout.Normalize("  "));
    }

    /// <summary>
    /// Spring forward: 02:30 does not exist on 2026-03-08 in the test zone, so the picker moves it forward by
    /// the one-hour gap (03:30 daylight, 07:30 UTC) and says so. A drift to 03:00 or a silent shift is the bug.
    /// </summary>
    [Fact]
    public void A_time_in_the_spring_forward_gap_moves_forward_by_the_gap_with_a_note()
    {
        var zone = Eastern();
        var model = new DateTimePickerModel("2026-03-07T12:00:00Z", zone, DateTimePickerPrecision.Minute, Now);
        Assert.Equal("", model.Note);
        model.PickDay(new DateTime(2026, 3, 8));
        model.SetHour(2);
        model.SetMinute(30);
        Assert.Contains("does not exist", model.Note, StringComparison.Ordinal);
        Assert.Contains("using 03:30", model.Note, StringComparison.Ordinal);
        Assert.Equal("2026-03-08T07:30:00Z", model.IsoUtc);
    }

    [Fact]
    public void An_ambiguous_fall_back_time_takes_the_first_occurrence()
    {
        var zone = Eastern();
        var model = new DateTimePickerModel("2026-10-31T12:00:00Z", zone, DateTimePickerPrecision.Minute, Now);
        model.PickDay(new DateTime(2026, 11, 1));
        model.SetHour(1);
        model.SetMinute(30);
        Assert.Contains("happens twice", model.Note, StringComparison.Ordinal);
        // The first 01:30 is still daylight time (UTC-4): 05:30 UTC, not 06:30.
        Assert.Equal("2026-11-01T05:30:00Z", model.IsoUtc);
        // The zone line names the occurrence the picker takes (daylight), not the local reading's standard time.
        Assert.Equal("Server time: UTC-04:00 (Test Daylight)", model.ZoneLabel());
    }

    [Fact]
    public void A_stored_value_shows_the_right_wall_clock_time_on_both_sides_of_a_change()
    {
        var zone = Eastern();
        Assert.Equal("Sun, Mar 8, 2026, 1:30 AM (UTC-05:00)",
            DateTimePickerModel.DisplayStored("2026-03-08T06:30:00Z", zone, DateTimePickerPrecision.Minute));
        Assert.Equal("Sun, Mar 8, 2026, 3:30 AM (UTC-04:00)",
            DateTimePickerModel.DisplayStored("2026-03-08T07:30:00Z", zone, DateTimePickerPrecision.Minute));
        Assert.Equal("Sun, Nov 1, 2026, 1:30 AM (UTC-04:00)",
            DateTimePickerModel.DisplayStored("2026-11-01T05:30:00Z", zone, DateTimePickerPrecision.Minute));
        Assert.Equal("Sun, Nov 1, 2026, 1:30 AM (UTC-05:00)",
            DateTimePickerModel.DisplayStored("2026-11-01T06:30:00Z", zone, DateTimePickerPrecision.Minute));
        var model = new DateTimePickerModel("2026-10-10T22:00:00Z", zone, DateTimePickerPrecision.Minute, Now);
        Assert.Equal("Server time: UTC-04:00 (Test Daylight)", model.ZoneLabel());
        Assert.Equal("Your time: UTC-04:00 (Test Daylight)", model.ZoneLabel("Your time"));
    }

    // ---- the builder ------------------------------------------------------------------------------

    [Fact]
    public void The_builder_adds_a_datetime_row_and_release_drops_its_functions()
    {
        var file = File("builder");
        var when = file.Bind("Menu", "When", "", "A time.");
        var count = file.Bind("Menu", "Count", 3, "A number.");
        bool seconds = true;
        var registration = ModSettingsPages.Register(_owner, Id, page => page
            .Section("Demo")
                .DateTime(when, seconds: () => seconds, zone: PlusTwo, label: "Event time", help: "Pick a time."));
        var item = registration.SectionSpecs[0].Items.Single();
        Assert.Equal(SettingControl.DateTime, item.Options.Control);
        Assert.NotNull(item.Options.DateTimeSeconds);
        Assert.NotNull(item.Options.DateTimeZone);
        Assert.Equal("Event time", item.Options.Label);

        var row = ModSettingsLayout.RowOf(Id, when, null);
        Assert.Equal(SettingControl.DateTime, ModSettingsLayout.ControlFor(row, item.Options));
        Assert.Equal(SettingControl.DateTime, ModSettingsLayout.DrawnControl(row, item.Options));
        Assert.True(ModSettingsLayout.IsDateTimeRow(row, item.Options));
        Assert.Equal("datetime", ModSettingsLayout.NameOf(SettingControl.DateTime));
        Assert.Equal(14, (int)SettingControl.DateTime);
        Assert.Equal(DateTimePickerPrecision.Second, ModSettingsLayout.DateTimePrecision(item.Options));
        seconds = false;
        Assert.Equal(DateTimePickerPrecision.Minute, ModSettingsLayout.DateTimePrecision(item.Options));
        Assert.Equal("Test Plus2", ModSettingsLayout.DateTimeZone(item.Options, TimeZoneInfo.Utc).Id);

        // Asked of a number, the date-time control falls back to the automatic one.
        var numberRow = ModSettingsLayout.RowOf(Id, count, null);
        var asked = new ModSettingOptions { Control = SettingControl.DateTime };
        Assert.Equal(ModSettingsLayout.ControlFor(numberRow), ModSettingsLayout.ControlFor(numberRow, asked));
        Assert.False(ModSettingsLayout.IsDateTimeRow(numberRow, asked));

        registration.Dispose();
        Assert.Null(item.Options.DateTimeSeconds);
        Assert.Null(item.Options.DateTimeZone);
    }

    // ---- the field and its popup ------------------------------------------------------------------

    [Fact]
    public void The_field_shows_the_value_and_done_raises_once_with_the_new_iso()
    {
        var panel = UiApi.Panel("Date test", _owner);
        var field = panel.DateTimeField("When", "", PlusTwo());
        var raised = new List<string>();
        field.OnChanged(raised.Add);
        var handle = (UiDateTimeField)field;
        Assert.Equal(UiDateTimeLayout.NotSet, handle.DisplayText);
        Assert.Equal(UiDateTimeLayout.NotSet, handle.Row.Cells.Single(c => c.Key == UiDateTimeLayout.CellValue).Text);
        Assert.Equal(UiDateTimeLayout.SetCaption, handle.Row.Cells.Single(c => c.Key == UiDateTimeLayout.CellOpen).Text);
        Assert.Equal(DateTimePickerPrecision.Minute, field.Precision);
        Assert.Equal("Test Plus2", field.Zone.Id);

        field.Value = "2026-10-10T16:00:00Z";
        Assert.Equal("Sat, Oct 10, 2026, 6:00 PM (UTC+02:00)", handle.DisplayText);
        Assert.Empty(raised);

        field.Open();
        Assert.True(field.IsOpen);
        var read = UiDateTimeBridge.Read();
        Assert.Equal("When", read["title"]);
        Assert.Equal("Time zone: UTC+02:00 (Test Plus2)", read["zone"]);
        Assert.Equal(42, ((List<object?>)read["cells"]!).Count);
        // Done with nothing changed raises nothing.
        UiDateTimeBridge.Done();
        Assert.False(field.IsOpen);
        Assert.Empty(raised);

        field.Open();
        UiDateTimeBridge.Day(11);
        UiDateTimeBridge.Time(hour: 9, minute: 5, second: null);
        var done = UiDateTimeBridge.Done();
        Assert.True((bool)done["ok"]!);
        Assert.Equal(new[] { "2026-10-11T07:05:00Z" }, raised);
        Assert.Equal("2026-10-11T07:05:00Z", field.Value);
        Assert.Equal("Sun, Oct 11, 2026, 9:05 AM (UTC+02:00)", handle.Row.Cells.Single(c => c.Key == UiDateTimeLayout.CellValue).Text);
        Assert.Equal(UiDateTimeLayout.ChangeCaption, handle.Row.Cells.Single(c => c.Key == UiDateTimeLayout.CellOpen).Text);
        panel.Dispose();
    }

    /// <summary>
    /// Month navigation and day and time changes rewrite cells in place: the same rows, new text and tones.
    /// Catches a picker that rebuilds itself (new rows) on every click.
    /// </summary>
    [Fact]
    public void Navigation_rewrites_the_popup_in_place()
    {
        var panel = UiApi.Panel("Date test", _owner);
        var field = panel.DateTimeField("When", "2026-10-10T18:00:00Z", TimeZoneInfo.Utc, DateTimePickerPrecision.Second);
        field.Open();
        int rowsBefore = _runtime.CellRows.Count(r => !r.Released);
        var month = _runtime.CellRow(UiDateTimeLayout.MonthRow)!;
        Assert.Equal("October 2026", month.Cells().Single(c => c.Key == UiDateTimeLayout.CellMonth).Text);

        UiDateTimeBridge.Month(delta: 1, year: null, month: null);
        Assert.Same(month, _runtime.CellRow(UiDateTimeLayout.MonthRow));
        Assert.Equal("November 2026", month.Cells().Single(c => c.Key == UiDateTimeLayout.CellMonth).Text);
        // November 2026 starts on a Sunday: cell 0 is the 1st, in month, normal tone; the 10th of October is gone.
        var week0 = _runtime.CellRow(UiDateTimeLayout.WeekRow(0))!;
        var first = week0.Cells().Single(c => c.Key == UiDateTimeLayout.DayKey(0));
        Assert.Equal("1", first.Text);
        Assert.Equal(CellTone.Normal, first.Tone);
        var trailing = _runtime.CellRow(UiDateTimeLayout.WeekRow(5))!.Cells().Single(c => c.Key == UiDateTimeLayout.DayKey(41));
        Assert.Equal(CellTone.Faint, trailing.Tone);

        UiDateTimeBridge.Day(17);
        var picked = _runtime.CellRow(UiDateTimeLayout.WeekRow(2))!.Cells().Single(c => c.Key == UiDateTimeLayout.DayKey(16));
        Assert.Equal("17", picked.Text);
        Assert.Equal(CellTone.Gold, picked.Tone);

        var read = UiDateTimeBridge.Time(hour: 7, minute: 58, second: 3);
        Assert.Equal("second", read["precision"]);
        var time = _runtime.CellRow(UiDateTimeLayout.TimeRow)!;
        Assert.Equal("07", time.Cells().Single(c => c.Key == "h").Text);
        Assert.Equal("58", time.Cells().Single(c => c.Key == "m").Text);
        Assert.Equal("03", time.Cells().Single(c => c.Key == "s").Text);
        Assert.Equal("2026-11-17T07:58:03Z", read["iso"]);
        Assert.Equal(rowsBefore, _runtime.CellRows.Count(r => !r.Released));
        Assert.Equal(1, _runtime.CellRows.Count(r => !r.Released && r.Text == UiDateTimeLayout.MonthRow));
        UiDateTimeBridge.Cancel();
        Assert.Equal("2026-10-10T18:00:00Z", field.Value);
        panel.Dispose();
    }

    [Fact]
    public void The_note_row_shows_only_for_a_gap_or_an_ambiguous_time()
    {
        var panel = UiApi.Panel("Date test", _owner);
        var field = panel.DateTimeField("When", "2026-03-07T12:00:00Z", Eastern());
        field.Open();
        var note = _runtime.CellRow(UiDateTimeLayout.NoteRow)!;
        Assert.False(note.Visible);
        UiDateTimeBridge.Day(8);
        UiDateTimeBridge.Time(hour: 2, minute: 30, second: null);
        Assert.True(note.Visible);
        Assert.Contains("does not exist", note.Cells().Single(c => c.Key == UiDateTimeLayout.CellText).Text, StringComparison.Ordinal);
        var read = UiDateTimeBridge.Read();
        Assert.True((bool)read["noteShown"]!);
        UiDateTimeBridge.Time(hour: 4, minute: null, second: null);
        Assert.False(note.Visible);
        panel.Dispose();
    }

    [Fact]
    public void Escape_cancels_and_disposing_the_field_closes_the_popup_and_runs_nothing()
    {
        var panel = UiApi.Panel("Date test", _owner);
        var field = panel.DateTimeField("When", "2026-10-10T18:00:00Z", TimeZoneInfo.Utc);
        int raised = 0;
        field.OnChanged(_ => raised++);

        field.Open();
        UiDateTimeBridge.Day(20);
        Assert.True(UiApi.TryEscapeHandlers());
        Assert.False(field.IsOpen);
        Assert.Equal("2026-10-10T18:00:00Z", field.Value);
        Assert.False(UiApi.TryEscapeHandlers());

        field.Open();
        var popup = ((UiDateTimeField)field).Picker!.Panel;
        UiDateTimeBridge.Day(21);
        panel.Dispose();
        Assert.True(field.IsDisposed);
        Assert.True(popup.IsDisposed);
        Assert.False(field.IsOpen);
        Assert.Null(UiDateTimeField.Current);
        Assert.False(UiApi.TryEscapeHandlers());
        Assert.Equal(0, raised);
        Assert.False((bool)UiDateTimeBridge.Read()["open"]!);
    }

    [Fact]
    public void A_grayed_field_does_not_open()
    {
        var panel = UiApi.Panel("Date test", _owner);
        var field = panel.DateTimeField("When", "", TimeZoneInfo.Utc);
        field.Interactable = false;
        field.Open();
        Assert.False(field.IsOpen);
        field.Interactable = true;
        field.Open();
        Assert.True(field.IsOpen);
        panel.Dispose();
    }

    [Fact]
    public void Opening_a_second_field_cancels_the_first()
    {
        var panel = UiApi.Panel("Date test", _owner);
        var a = panel.DateTimeField("A", "2026-10-10T18:00:00Z", TimeZoneInfo.Utc);
        var b = panel.DateTimeField("B", "2026-10-10T18:00:00Z", TimeZoneInfo.Utc);
        a.Open();
        b.Open();
        Assert.False(a.IsOpen);
        Assert.True(b.IsOpen);
        Assert.Equal("B", UiDateTimeBridge.Read()["title"]);
        panel.Dispose();
    }

    [Fact]
    public void The_field_row_has_the_grid_columns_and_the_reset_gutter()
    {
        var plain = UiDateTimeLayout.FieldRow("k", "Event time", "Not set", "Set", resettable: false);
        Assert.Equal(new[] { "name-lead", "caption", "value", "open", "gutter" }, plain.Cells.Select(c => c.Key).ToArray());
        Assert.Equal(CellSkin.ValueBox, plain.Find("value")!.Skin);
        // GRIDROW1 (live 2026-10-11): the caption is in the grid names' gray, not the brighter text white.
        Assert.Equal(CellTone.SettingName, plain.Find("caption")!.Tone);
        Assert.True(plain.Find("value")!.ReadOnly);
        Assert.Equal(CellSkin.KeyCap, plain.Find("open")!.Skin);
        Assert.Equal(ModSettingsRowGrid.RowPitch, plain.Height);
        var reset = UiDateTimeLayout.FieldRow("k", "Event time", "Not set", "Set", resettable: true);
        Assert.Equal(CellSkin.ResetIcon, reset.Find("reset")!.Skin);
        Assert.Equal(9, UiDateTimeLayout.WeekRowSpec(0, DateTimePickerModel.Grid(2026, 10), DialogLayout.ContentRowWidth()).Cells.Count);
        Assert.Equal(new[] { "dialog-left", "time-label", "h-minus", "h", "h-plus", "colon-1", "m-minus", "m", "m-plus", "dialog-right" },
            UiDateTimeLayout.TimeRowSpec(seconds: false).Cells.Select(c => c.Key).ToArray());
        Assert.Contains(UiDateTimeLayout.TimeRowSpec(seconds: true).Cells, c => c.Key == "s-plus");
    }

    /// <summary>
    /// GRIDROW1 (live 2026-10-11): the field row's caption starts on the settings grid's name inset and its value box on the
    /// grid's control column, at every row width a page or a game window gives it, so it lines up with the rows around it.
    /// Catches the caption drawn from the row's edge, or the value box on a column of its own.
    /// </summary>
    [Theory]
    [InlineData(560f)]
    [InlineData(760f)]
    [InlineData(1100f)]
    public void The_field_row_sits_on_the_settings_grid_columns(float rowWidth)
    {
        var row = UiDateTimeLayout.FieldRow("k", "Reminder", "Not set", "Set", resettable: false);
        var placed = CellLayout.Resolve(rowWidth, row.Cells, row.RightInset);
        var grid = ModSettingsRowGrid.For(rowWidth);
        float left = -rowWidth * 0.5f;
        var caption = placed.Single(p => p.Key == "caption");
        var value = placed.Single(p => p.Key == "value");
        var open = placed.Single(p => p.Key == "open");
        Assert.True(caption.Visible && value.Visible && open.Visible, string.Join(", ", placed));
        Assert.InRange(caption.Left - left, ModSettingsRowGrid.NameInset - 0.5f, ModSettingsRowGrid.NameInset + 0.5f);
        Assert.True(Math.Abs(value.Left - left - grid.ControlLeft) <= 0.5f, "controlLeft=" + grid.ControlLeft + " barRight=" + grid.BarRight + " right=" + grid.Right + " " + string.Join(", ", placed));
        Assert.True(open.Right - left <= grid.BarRight + 0.5f, string.Join(", ", placed));
    }

    // ---- the settings page -----------------------------------------------------------------------

    private ConfigEntry<string> _event = null!;

    private void RegisterPersonal()
    {
        var file = File("personal");
        _event = file.Bind("Menu", "EventTime", "", "A date and time as ISO 8601 UTC.");
        _handles.Add(ModSettingsPages.Register(_owner, Id, page => page
            .Section("Demo")
                .DateTime(_event, zone: PlusTwo, label: "Event time", help: "A demo of the date-time picker.")));
    }

    /// <summary>
    /// A personal date-time row draws the date field, reads back control datetime with its value, zone and
    /// precision, and Done commits the ISO UTC value through the settings write path. Catches a row drawn as
    /// a plain text field, a commit in local time, or a value written without the page seeing it.
    /// </summary>
    [Fact]
    public void A_personal_datetime_row_commits_the_iso_value_through_the_page()
    {
        RegisterPersonal();
        Open();
        var row = Setting(PageRows(), Id + "/Menu/EventTime");
        Assert.Equal("datetime", row.GetProperty("control").GetString());
        Assert.Equal("", row.GetProperty("iso").GetString());
        Assert.Equal("Not set", row.GetProperty("display").GetString());
        Assert.Equal("Your time: UTC+02:00 (Test Plus2)", row.GetProperty("zone").GetString());
        Assert.False(row.GetProperty("seconds").GetBoolean());
        Assert.Equal("Event time", row.GetProperty("displayName").GetString());

        var open = UiDateTimeBridge.Open("Event time");
        Assert.True((bool)open["open"]!);
        Assert.Equal("Event time", open["title"]);
        UiDateTimeBridge.Month(delta: null, year: 2027, month: 3);
        UiDateTimeBridge.Day(14);
        UiDateTimeBridge.Time(hour: 9, minute: 30, second: null);
        var done = UiDateTimeBridge.Done();
        Assert.Equal("2027-03-14T07:30:00Z", done["iso"]);
        Assert.Equal("2027-03-14T07:30:00Z", _event.Value);

        row = Setting(PageRows(), Id + "/Menu/EventTime");
        Assert.Equal("2027-03-14T07:30:00Z", row.GetProperty("iso").GetString());
        Assert.Equal("Sun, Mar 14, 2027, 9:30 AM (UTC+02:00)", row.GetProperty("display").GetString());
        Assert.True(row.GetProperty("modified").GetBoolean());
        Assert.Equal("changed", row.GetProperty("reset").GetString());
        Assert.Contains(ModSettingsView.SavedNote, row.GetProperty("helpLine").GetString(), StringComparison.Ordinal);

        // A canceled pick writes nothing; a page rebuild closes an open picker without a write.
        UiDateTimeBridge.Open("Event time");
        UiDateTimeBridge.Day(20);
        UiDateTimeBridge.Cancel();
        Assert.Equal("2027-03-14T07:30:00Z", _event.Value);
        UiDateTimeBridge.Open("Event time");
        UiDateTimeBridge.Day(21);
        ModsMenu.Close();
        Assert.False((bool)UiDateTimeBridge.Read()["open"]!);
        Assert.Equal("2027-03-14T07:30:00Z", _event.Value);
    }

    [Fact]
    public void A_grayed_personal_datetime_row_refuses_to_open()
    {
        var file = File("grayed");
        _event = file.Bind("Menu", "EventTime", "", "");
        _handles.Add(ModSettingsPages.Register(_owner, Id, page => page
            .Section("Demo").DateTime(_event, label: "Event time", enableIf: () => false)));
        Open();
        var result = UiDateTimeBridge.Open("Event time");
        Assert.False((bool)result["ok"]!);
        Assert.False((bool)UiDateTimeBridge.Read()["open"]!);
    }

    // ---- administrator rows ----------------------------------------------------------------------

    private ConfigEntry<string> _next = null!;
    private ConfigEntry<int> _limit = null!;
    private ConfigEntry<string> _mode = null!;

    private void RegisterServer()
    {
        var file = File("server");
        _next = file.Bind("Menu", "NextReset", "", "The next reset as ISO 8601 UTC.");
        _limit = file.Bind("Menu", "ExampleLimit", 10, "A limit.");
        _mode = file.Bind("Menu", "Mode", "Short", "A mode.");
        _handles.Add(ConfigSync.Register(Id, _next));
        _handles.Add(ConfigSync.Register(Id, _limit));
        _handles.Add(ConfigSync.Register(Id, _mode));
        _handles.Add(ModSettingsPages.Register(_owner, Id, page => page
            .Section("Server")
                .DateTime(_next, zone: Eastern, label: "Next reset", scope: SettingScope.Server, help: "When the server resets.")
                .Slider(_limit, 1, 20, scope: SettingScope.Server)
                .Radio(_mode, choices: new[] { "Short", "Full" }, scope: SettingScope.Server)));
    }

    private static void ServerSent(string key, string value) =>
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Id, "Menu", key, value) }, snapshot: false);

    private static SettingsScopeContext Joined(bool admin) => new()
    {
        Connection = SettingConnection.Progenitor,
        ServerPart = ServerPartState.Running,
        CanEditServerWide = admin,
        ServerName = "Progenitor Forms Test",
        ServerAddress = "127.0.0.1:9976",
    };

    /// <summary>
    /// An administrator's date-time row is the picker in the server's zone, and Done sends the ISO value
    /// through the request path with the Apply row's waiting and saved notes. Catches a server-wide date
    /// edited as a raw text field, or a request sent in local time.
    /// </summary>
    [Fact]
    public void An_admin_datetime_row_sends_the_iso_value_to_the_server()
    {
        RegisterServer();
        _context = Joined(admin: true);
        ConfigSync.ApplyRecords(new[]
        {
            new ConfigSyncRecord(Id, "Menu", "NextReset", "2026-10-10T22:00:00Z"),
            new ConfigSyncRecord(Id, "Menu", "ExampleLimit", "12"),
            new ConfigSyncRecord(Id, "Menu", "Mode", "Short"),
        }, snapshot: true);
        Open();
        var rows = PageRows();
        var next = Setting(rows, Id + "/Menu/NextReset");
        Assert.Equal("datetime", next.GetProperty("control").GetString());
        Assert.Equal("datetime", next.GetProperty("editor").GetString());
        Assert.Equal("ServerRequest", next.GetProperty("editPath").GetString());
        Assert.Equal("Sat, Oct 10, 2026, 6:00 PM (UTC-04:00)", next.GetProperty("display").GetString());
        Assert.Equal("Server time: UTC-04:00 (Test Daylight)", next.GetProperty("zone").GetString());

        UiDateTimeBridge.Open("Next reset");
        Assert.Equal("Server time: UTC-04:00 (Test Daylight)", UiDateTimeBridge.Read()["zone"]);
        UiDateTimeBridge.Day(11);
        UiDateTimeBridge.Done();
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        Assert.Equal(Id + "/Menu/NextReset", request.RowId);
        Assert.Equal("2026-10-11T22:00:00Z", request.Value);
        Assert.Contains(ModSettingsView.WaitingNote, Setting(PageRows(), Id + "/Menu/NextReset").GetProperty("helpLine").GetString(), StringComparison.Ordinal);

        ServerSettings.AcceptResponse(request.WithResult(new(true, "2026-10-11T22:00:00Z", "")));
        next = Setting(PageRows(), Id + "/Menu/NextReset");
        Assert.Contains(ModSettingsView.ServerSavedNote, next.GetProperty("helpLine").GetString(), StringComparison.Ordinal);

        // The choice row is its control now; the number row keeps its field and Apply (one request per
        // stepper click would spend the server's edit rate limit).
        rows = PageRows();
        var limit = Setting(rows, Id + "/Menu/ExampleLimit");
        Assert.Equal("server-request", limit.GetProperty("control").GetString());
        Assert.False(limit.TryGetProperty("editor", out _));
        var mode = Setting(rows, Id + "/Menu/Mode");
        Assert.Equal("radio", mode.GetProperty("editor").GetString());
        Assert.Equal(new[] { "Short", "Full" }, mode.GetProperty("choices").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void An_admin_choice_pick_sends_a_request_a_refusal_puts_the_value_back_and_a_number_keeps_apply()
    {
        RegisterServer();
        _context = Joined(admin: true);
        ConfigSync.ApplyRecords(new[]
        {
            new ConfigSyncRecord(Id, "Menu", "NextReset", ""),
            new ConfigSyncRecord(Id, "Menu", "ExampleLimit", "12"),
            new ConfigSyncRecord(Id, "Menu", "Mode", "Short"),
        }, snapshot: true);
        Open();

        Assert.True(_runtime.FireOption("radio", "Mode", 1));
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var pick));
        Assert.Equal(Id + "/Menu/Mode", pick.RowId);
        Assert.Equal("Full", pick.Value);
        ServerSettings.AcceptResponse(pick.WithResult(new(false, "", "Only server admins can change this.")));
        Assert.Equal("Short", _runtime.ValueOf("radio", "Mode"));
        Assert.Contains("Only server admins can change this.",
            Setting(PageRows(), Id + "/Menu/Mode").GetProperty("helpLine").GetString(), StringComparison.Ordinal);

        // The number row is not a stepper that sends per change: nothing is sent until Apply.
        _sent = null;
        Assert.False(_runtime.FireTextOn("numeric", "Example limit", "13"));
        Assert.Null(_sent);
    }

    [Fact]
    public void A_player_who_is_not_an_admin_reads_the_date_as_a_wall_clock_time()
    {
        RegisterServer();
        _context = Joined(admin: false);
        ServerSent("NextReset", "2026-10-10T22:00:00Z");
        Open();
        var next = Setting(PageRows(), Id + "/Menu/NextReset");
        Assert.Equal("readonly", next.GetProperty("control").GetString());
        Assert.Equal("2026-10-10T22:00:00Z", next.GetProperty("iso").GetString());
        Assert.Equal("Admin only · Sat, Oct 10, 2026, 6:00 PM (UTC-04:00)", _runtime.ValueOf("info", "Next reset"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private ConfigFile File(string name) => new(Path.Combine(_dir, name + "-" + Guid.NewGuid().ToString("N") + ".cfg"), saveOnInit: true);

    private static void Open()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(Id);
    }

    private JsonElement PageRows()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static JsonElement Setting(JsonElement rows, string id)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty(ModSettingsView.RowSetting, out var setting)) continue;
            if (string.Equals(setting.GetProperty("id").GetString(), id, StringComparison.Ordinal)) return setting;
        }

        throw new Xunit.Sdk.XunitException("no setting row " + id);
    }
}
