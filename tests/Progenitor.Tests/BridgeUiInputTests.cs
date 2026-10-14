using System;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// BRIDGEUI1: the game-free halves of <c>ui_input</c>, <c>ui_read</c>, <c>display</c>, <c>screenshot</c>, the new
/// <c>nav</c> actions and <c>widget_click</c>'s modifiers, and Core's synthetic input state (lease, wheel edges, key
/// taps, navigation holds).
/// </summary>
public sealed class BridgeUiInputTests : IDisposable
{
    private long _now = 1_000_000;

    public BridgeUiInputTests()
    {
        UiSyntheticInput.Clock = () => _now;
        UiSyntheticInput.BeforeEnd = null;
        UiSyntheticInput.End("test");
        UiSyntheticInput.ClearNav();
    }

    public void Dispose()
    {
        UiSyntheticInput.BeforeEnd = null;
        UiSyntheticInput.End("test");
        UiSyntheticInput.ClearNav();
        UiSyntheticInput.Clock = () => Environment.TickCount64;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static UiInputPlan.Request Parse(string json) => UiInputPlan.Parse(Json(json));

    // ---- ui_input parsing --------------------------------------------------------------------------

    [Fact]
    public void A_click_on_a_cell_takes_the_row_and_cell_and_defaults_to_the_left_button_at_the_center()
    {
        var r = Parse("{\"action\":\"click\",\"row\":\"demo-table:row0\",\"cell\":\"e:reserve\"}");
        Assert.Equal(UiInputPlan.Action.Click, r.Action);
        Assert.Equal("demo-table:row0", r.At!.Row);
        Assert.Equal("e:reserve", r.At.Cell);
        Assert.Equal(1, r.Button);
        Assert.Equal(0.5f, r.At.Fx);
        Assert.Equal(0.5f, r.At.Fy);
        Assert.Equal(UiInputPlan.DefaultSettleFrames, r.SettleFrames);
        Assert.Equal(UiInputPlan.DefaultHoldFrames, r.HoldFrames);
        Assert.False(r.ReleaseAfter);
    }

    [Fact]
    public void Modifiers_take_an_array_or_a_joined_string_and_refuse_alt()
    {
        Assert.Equal(UiInputPlan.Shift, Parse("{\"action\":\"click\",\"row\":\"h\",\"modifiers\":[\"shift\"]}").Modifiers);
        Assert.Equal(UiInputPlan.Shift | UiInputPlan.Ctrl, Parse("{\"action\":\"click\",\"row\":\"h\",\"modifiers\":\"Ctrl+Shift\"}").Modifiers);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"row\":\"h\",\"modifiers\":[\"alt\"]}"));
        Assert.Equal(UiInputPlan.Shift, Parse("{\"action\":\"key\",\"key\":\"End\",\"modifiers\":[\"shift\"]}").Modifiers);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"row\":\"r\",\"notches\":1,\"modifiers\":[\"shift\"]}"));
    }

    [Fact]
    public void Hover_is_move_and_a_panel_point_takes_fractions_and_offsets()
    {
        var r = Parse("{\"action\":\"hover\",\"panel\":\"Explained words\",\"fx\":0.1,\"fy\":0.9,\"dx\":-4}");
        Assert.Equal(UiInputPlan.Action.Move, r.Action);
        Assert.Equal("Explained words", r.At!.Panel);
        Assert.Equal(0.1f, r.At.Fx, 3);
        Assert.Equal(0.9f, r.At.Fy, 3);
        Assert.Equal(-4f, r.At.Dx);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"move\",\"panel\":\"p\",\"fx\":1.5}"));
    }

    [Fact]
    public void A_target_is_one_of_row_panel_or_point()
    {
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\"}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"row\":\"r\",\"x\":1,\"y\":1}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"x\":1}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"cell\":\"c\"}"));
        var p = Parse("{\"action\":\"click\",\"x\":10,\"y\":20,\"button\":\"right\"}");
        Assert.True(p.At!.HasPoint);
        Assert.Equal(2, p.Button);
    }

    [Fact]
    public void A_drag_needs_a_destination_and_takes_steps()
    {
        var r = Parse("{\"action\":\"drag\",\"row\":\"demo-list:row0\",\"cell\":\"grip\",\"to\":{\"row\":\"demo-list:row3\",\"fy\":0.1},\"steps\":20}");
        Assert.Equal("demo-list:row3", r.To!.Row);
        Assert.Equal(0.1f, r.To.Fy, 3);
        Assert.Equal(20, r.Steps);
        var p = Parse("{\"action\":\"drag\",\"x\":1,\"y\":2,\"toX\":30,\"toY\":40}");
        Assert.True(p.To!.HasPoint);
        Assert.Equal(30f, p.To.X);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"drag\",\"x\":1,\"y\":2}"));
    }

    [Fact]
    public void Wheel_key_type_and_focus_validate_their_arguments()
    {
        Assert.Equal(-3, Parse("{\"action\":\"wheel\",\"row\":\"r\",\"notches\":-3}").Notches);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"row\":\"r\",\"notches\":0}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"row\":\"r\",\"notches\":21}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"wheel\",\"row\":\"r\"}"));
        Assert.Equal("Escape", Parse("{\"action\":\"key\",\"key\":\" Escape \"}").Key);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"key\"}"));
        Assert.Equal("40", Parse("{\"action\":\"type\",\"text\":\"40\"}").Text);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"type\",\"text\":\"\"}"));
        Assert.Equal(UiInputPlan.Action.Focus, Parse("{\"action\":\"focus\",\"row\":\"demo-md:row0\",\"cell\":\"m-search\"}").Action);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"jump\"}"));
    }

    [Fact]
    public void The_lease_time_is_bounded()
    {
        Assert.Equal(60_000, Parse("{\"action\":\"status\",\"leaseMs\":60000}").LeaseMs);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"status\",\"leaseMs\":100}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"status\",\"leaseMs\":400000}"));
    }

    // ---- timelines ---------------------------------------------------------------------------------

    [Fact]
    public void A_click_settles_presses_holds_releases_and_waits()
    {
        var r = Parse("{\"action\":\"click\",\"x\":5,\"y\":6,\"settleFrames\":3,\"holdFrames\":4}");
        var t = UiInputPlan.Timeline(r, 5, 6);
        Assert.Equal(new[] { "0:Move(5,6)", "3:Down", "7:Up", "9:Done" }, t.Select(s => s.ToString()).ToArray());
    }

    [Fact]
    public void A_drag_moves_one_step_a_frame_and_releases_after_the_last()
    {
        var r = Parse("{\"action\":\"drag\",\"x\":0,\"y\":0,\"toX\":40,\"toY\":0,\"steps\":4,\"settleFrames\":2}");
        var t = UiInputPlan.Timeline(r, 0, 0, 40, 0);
        Assert.Equal(new[] { "0:Move(0,0)", "2:Down", "3:Move(10,0)", "4:Move(20,0)", "5:Move(30,0)", "6:Move(40,0)", "7:Up", "9:Done" },
            t.Select(s => s.ToString()).ToArray());
    }

    [Fact]
    public void A_kept_down_drag_has_no_release_and_up_takes_no_target()
    {
        var r = Parse("{\"action\":\"drag\",\"x\":0,\"y\":0,\"toX\":10,\"toY\":0,\"steps\":2,\"keepDown\":true}");
        Assert.True(r.KeepDown);
        Assert.DoesNotContain(UiInputPlan.Timeline(r, 0, 0, 10, 0), s => s.Kind == UiInputPlan.StepKind.Up);
        Assert.Equal(UiInputPlan.Action.Up, Parse("{\"action\":\"up\"}").Action);
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"x\":1,\"y\":1,\"keepDown\":true}"));
    }

    [Fact]
    public void A_settle_of_zero_still_gives_the_pointer_one_frame_before_the_press()
    {
        var r = Parse("{\"action\":\"click\",\"x\":5,\"y\":6,\"settleFrames\":0}");
        Assert.Equal(1, UiInputPlan.Timeline(r, 5, 6)[1].Frame);
    }

    // ---- Core synthetic input ----------------------------------------------------------------------

    [Fact]
    public void The_lease_runs_out_and_ending_drops_every_button_key_and_notch()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 1_000));
        UiSyntheticInput.MoveTo(10, 20);
        UiSyntheticInput.SetButtons(UiSyntheticButtons.Left);
        UiSyntheticInput.SetModifiers(UiClickModifiers.Shift);
        UiSyntheticInput.QueueWheel(2);
        UiSyntheticInput.Tap(UiKey.Escape, 5);
        string? endedWith = null;
        UiSyntheticInput.BeforeEnd = reason => endedWith = reason;
        _now += 999;
        Assert.False(UiSyntheticInput.Tick());
        Assert.True(UiSyntheticInput.Active);
        _now += 1;
        Assert.True(UiSyntheticInput.Tick());
        Assert.Equal("timeout", endedWith);
        Assert.False(UiSyntheticInput.Active);
        Assert.False(UiSyntheticInput.HasPointer);
        Assert.Equal(UiSyntheticButtons.None, UiSyntheticInput.Buttons);
        Assert.Equal(UiClickModifiers.None, UiSyntheticInput.Modifiers);
        Assert.Equal(0, UiSyntheticInput.PendingNotches);
        Assert.Equal(0, UiSyntheticInput.HeldKeyCount);
        Assert.Equal("timeout", UiSyntheticInput.LastEnd);
    }

    [Fact]
    public void Each_action_renews_the_lease_and_another_owner_is_refused()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 1_000));
        _now += 900;
        Assert.Null(UiSyntheticInput.Begin("bridge", 1_000));
        _now += 900;
        Assert.True(UiSyntheticInput.Active);
        Assert.NotNull(UiSyntheticInput.Begin("other", 1_000));
        UiSyntheticInput.End("release");
        Assert.Null(UiSyntheticInput.Begin("other", 1_000));
    }

    [Fact]
    public void Nothing_is_taken_without_a_lease()
    {
        UiSyntheticInput.MoveTo(1, 2);
        UiSyntheticInput.SetButtons(UiSyntheticButtons.Left);
        UiSyntheticInput.QueueWheel(1);
        Assert.False(UiSyntheticInput.Tap(UiKey.Enter, 0));
        Assert.False(UiSyntheticInput.HasPointer);
        Assert.Equal(UiSyntheticButtons.None, UiSyntheticInput.Buttons);
        Assert.Equal(0, UiSyntheticInput.PendingNotches);
    }

    [Fact]
    public void Wheel_notches_come_one_a_sample_with_a_zero_between_so_each_is_an_edge()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        UiSyntheticInput.QueueWheel(-1);
        UiSyntheticInput.QueueWheel(-2);
        Assert.Equal(3, UiSyntheticInput.PendingNotches);
        // Three notches, three edges: never one -240 reading the pump would count once.
        Assert.Equal(-120f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(-120f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(-120f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0, UiSyntheticInput.PendingNotches);
    }

    [Fact]
    public void A_key_tap_is_held_for_its_frames()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        Assert.True(UiSyntheticInput.Tap(UiKey.Escape, 100, 2));
        Assert.False(UiSyntheticInput.KeyHeld(UiKey.Escape, 99));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Escape, 100));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Escape, 101));
        Assert.False(UiSyntheticInput.KeyHeld(UiKey.Escape, 102));
        UiSyntheticInput.PruneKeys(102);
        Assert.Equal(1, UiSyntheticInput.HeldKeyCount); // kept one frame past its end
        UiSyntheticInput.PruneKeys(103);
        Assert.Equal(0, UiSyntheticInput.HeldKeyCount);
    }

    [Fact]
    public void Back_to_back_taps_of_one_key_keep_a_released_frame_between_them()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        Assert.True(UiSyntheticInput.Tap(UiKey.Backspace, 100, 2)); // held 100, 101
        Assert.True(UiSyntheticInput.Tap(UiKey.Backspace, 101, 2)); // queued after the released frame 102
        Assert.False(UiSyntheticInput.KeyHeld(UiKey.Backspace, 102));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Backspace, 103));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Backspace, 104));
        Assert.Equal(105, UiSyntheticInput.TapUntil(UiKey.Backspace));
        Assert.True(UiSyntheticInput.Tap(UiKey.Backspace, 106, 2));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Backspace, 106));
    }

    [Fact]
    public void Escape_goes_to_the_escape_hook_at_tap_time_only_while_a_panel_is_shown()
    {
        // Decided when the key is tapped, so nothing is left pending for a panel that opens later.
        Assert.True(UiSyntheticInput.ForwardsEscape(UiKey.Escape, 1));
        Assert.False(UiSyntheticInput.ForwardsEscape(UiKey.Escape, 0));
        Assert.False(UiSyntheticInput.ForwardsEscape(UiKey.Enter, 3));
    }

    [Fact]
    public void Queued_notches_are_drained_on_failure_and_on_the_lease_end()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        UiSyntheticInput.QueueWheel(3);
        Assert.Equal(3, UiSyntheticInput.DrainWheel());
        Assert.Equal(0, UiSyntheticInput.PendingNotches);
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        UiSyntheticInput.QueueWheel(-2);
        UiSyntheticInput.End("release");
        Assert.Equal(0, UiSyntheticInput.PendingNotches);
    }

    [Fact]
    public void Text_queued_for_one_field_is_orphaned_when_another_field_or_none_has_the_keyboard()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        UiSyntheticInput.NoteTextFor(42);
        Assert.False(UiSyntheticInput.TextOrphaned(42, 1));   // still its field
        Assert.True(UiSyntheticInput.TextOrphaned(0, 1));     // the field closed (a dialog took Enter)
        Assert.True(UiSyntheticInput.TextOrphaned(7, 1));     // another field took focus
        Assert.False(UiSyntheticInput.TextOrphaned(7, 0));    // nothing queued
        UiSyntheticInput.NoteQueue(0);                        // drained: the bridge owns nothing any more
        Assert.Equal(0, UiSyntheticInput.TextOwner);
        Assert.False(UiSyntheticInput.TextOrphaned(7, 5));    // a later mods_ui type is not ours to drop
        UiSyntheticInput.NoteTextFor(42);
        UiSyntheticInput.NoteQueue(2);
        Assert.Equal(42, UiSyntheticInput.TextOwner);
        UiSyntheticInput.End("release");
        Assert.Equal(0, UiSyntheticInput.TextOwner);
    }

    [Fact]
    public void Moves_and_button_changes_bump_the_pointer_serial()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        int s0 = UiSyntheticInput.PointerSerial;
        UiSyntheticInput.MoveTo(5, 5);
        UiSyntheticInput.MoveTo(5, 5);
        int s1 = UiSyntheticInput.PointerSerial;
        Assert.Equal(s0 + 1, s1);
        UiSyntheticInput.SetButtons(UiSyntheticButtons.Right);
        Assert.Equal(s1 + 1, UiSyntheticInput.PointerSerial);
    }

    [Fact]
    public void Navigation_holds_live_only_inside_the_lease()
    {
        UiSyntheticInput.HoldNav(UiSyntheticNav.Down, 500);
        Assert.Equal(UiSyntheticNav.None, UiSyntheticInput.NavHeld);
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        Assert.Equal(UiSyntheticNav.Down, UiSyntheticInput.NavHeld);
        UiSyntheticInput.End("user-activity");
        Assert.Equal(UiSyntheticNav.None, UiSyntheticInput.NavHeld);
    }

    [Fact]
    public void Navigation_holds_and_the_stick_expire_on_their_own_clock()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        UiSyntheticInput.HoldNav(UiSyntheticNav.Down, 500);
        UiSyntheticInput.HoldStick(-2f, 300);
        Assert.Equal(UiSyntheticNav.Down, UiSyntheticInput.NavHeld);
        Assert.Equal(-1f, UiSyntheticInput.StickHeld);
        _now += 300;
        Assert.Equal(0f, UiSyntheticInput.StickHeld);
        Assert.Equal(UiSyntheticNav.Down, UiSyntheticInput.NavHeld);
        _now += 200;
        Assert.Equal(UiSyntheticNav.None, UiSyntheticInput.NavHeld);
    }

    // ---- Caret lines (Up, Down, Home, End in a multi-line field) ------------------------------------

    [Fact]
    public void Home_and_end_stay_on_the_caret_line()
    {
        const string text = "first\nsecond line\nx";
        var starts = UiCaretLines.BreakStarts(text);
        Assert.Equal(new[] { 0, 6, 18 }, starts);
        Assert.Equal(6, UiCaretLines.Move(UiKey.Home, text, starts, 9));
        Assert.Equal(17, UiCaretLines.Move(UiKey.End, text, starts, 9));
        Assert.Equal(19, UiCaretLines.Move(UiKey.End, text, starts, 18));
        Assert.Equal(-1, UiCaretLines.Move(UiKey.LeftArrow, text, starts, 9));
    }

    [Fact]
    public void Up_and_down_keep_the_column_clamped_to_the_line()
    {
        const string text = "first\nsecond line\nx";
        var starts = UiCaretLines.BreakStarts(text);
        Assert.Equal(3, UiCaretLines.Move(UiKey.UpArrow, text, starts, 9));    // "sec|ond" -> "fir|st"
        Assert.Equal(5, UiCaretLines.Move(UiKey.UpArrow, text, starts, 16));   // past "first"'s end: its end
        Assert.Equal(19, UiCaretLines.Move(UiKey.DownArrow, text, starts, 9)); // onto "x", clamped to its end
        Assert.Equal(0, UiCaretLines.Move(UiKey.UpArrow, text, starts, 3));    // first line: to the start
        Assert.Equal(19, UiCaretLines.Move(UiKey.DownArrow, text, starts, 18)); // last line: to the end
    }

    [Fact]
    public void Drawn_lines_from_wrapping_are_lines_too()
    {
        // "aaaa bbbb" wrapped after "aaaa ": the drawn line starts are 0 and 5, with no line break between them.
        const string text = "aaaa bbbb";
        var starts = new[] { 0, 5 };
        Assert.Equal(5, UiCaretLines.Move(UiKey.End, text, starts, 2));
        Assert.Equal(5, UiCaretLines.Move(UiKey.Home, text, starts, 7));
        Assert.Equal(7, UiCaretLines.Move(UiKey.DownArrow, text, starts, 2));
    }

    // ---- display: the engine's remembered window values -----------------------------------------------

    [Fact]
    public void Registry_values_round_trip_and_only_screen_values_are_touched()
    {
        Assert.True(DisplayPlan.IsScreenValue("Screenmanager Resolution Width_h182942802"));
        Assert.False(DisplayPlan.IsScreenValue("UnityGraphicsQuality_h1669003810"));
        var dword = DisplayPlan.DecodeRegistry(DisplayPlan.EncodeRegistry("dword", 2560))!.Value;
        Assert.Equal(("dword", (object)2560), (dword.Kind, dword.Value));
        var binary = DisplayPlan.DecodeRegistry(DisplayPlan.EncodeRegistry("binary", new byte[] { 1, 2, 3 }))!.Value;
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])binary.Value);
        Assert.Equal(-5L, DisplayPlan.DecodeRegistry("qword:-5")!.Value.Value);
        Assert.Null(DisplayPlan.EncodeRegistry("dword", "x"));
        Assert.Null(DisplayPlan.DecodeRegistry("dword:notanumber"));
        Assert.Null(DisplayPlan.DecodeRegistry("multistring:a"));
        var b = new DisplayPlan.Baseline { Width = 2560, Height = 1440, Mode = 3, HasWindowPosition = true, WindowX = 100, WindowY = 50 };
        b.Registry["Screenmanager Window Position X_h4088080503"] = "dword:100";
        var back = DisplayPlan.ParseBaseline(DisplayPlan.SerializeBaseline(b))!;
        Assert.True(back.HasWindowPosition);
        Assert.Equal(50, back.WindowY);
        Assert.Equal("dword:100", back.Registry["Screenmanager Window Position X_h4088080503"]);
    }

    [Fact]
    public void A_pending_baseline_from_an_earlier_run_is_restored_only_while_the_window_is_the_test_setting()
    {
        var b = new DisplayPlan.Baseline { Width = 3840, Height = 1080, Mode = 1, HasRequest = true, RequestedWidth = 2560, RequestedHeight = 1440, RequestedMode = 3 };
        var back = DisplayPlan.ParseBaseline(DisplayPlan.SerializeBaseline(b))!;
        Assert.True(back.HasRequest);
        Assert.Equal(2560, back.RequestedWidth);
        Assert.True(DisplayPlan.PendingOnLoad(back, 2560, 1440, 3).Restore);
        var moved = DisplayPlan.PendingOnLoad(back, 3840, 1080, 1);   // the user (or a restore) changed it since
        Assert.False(moved.Restore);
        Assert.Contains("not the test setting", moved.Why);
        Assert.False(DisplayPlan.PendingOnLoad(back, 2560, 1440, 1).Restore); // same size, other mode
        Assert.False(DisplayPlan.PendingOnLoad(new DisplayPlan.Baseline { Width = 1920, Height = 1080, Mode = 3 }, 1920, 1080, 3).Restore);
    }

    [Fact]
    public void Display_reports_focus_from_one_reading_and_finds_the_game_window_by_its_class()
    {
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "DisplayOps.cs");
        SourcePin.AssertNoStatementContaining(ops, "Application.isFocused");
        SourcePin.AssertUncommented(ops, "Before = GameWindow.Focus()");
        string window = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "GameWindow.cs");
        SourcePin.AssertNoStatementContaining(window, "MainWindowHandle");
        SourcePin.AssertUncommented(window, "GameWindowClass");
        SourcePin.AssertUncommented(SourcePin.ReadFile("src", "Progenitor.Dev", "Plugin.cs"), "Ops.DisplayOps.OnLoad();");
    }

    // ---- The Unity half, pinned in source (it needs the game to run) ------------------------------

    [Fact]
    public void While_leased_the_input_module_is_held_off_and_given_back_at_the_end()
    {
        string frame = SourcePin.MethodBody(Driver(), "private static void SyntheticFrame(");
        SourcePin.AssertUncommented(frame, "HoldInputModule();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(Driver(), "private static void SyntheticBeforeEnd("), "ReleaseInputModule();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(Driver(), "private static void StopSyntheticDriver("), "ReleaseInputModule();");
    }

    [Fact]
    public void A_first_click_keeps_the_kits_select_all_and_only_a_later_click_places_the_caret()
    {
        string note = SourcePin.MethodBody(Driver(), "private static void NoteCaretClick(");
        SourcePin.AssertUncommented(note, "if (FieldId(FocusedArmedField()) != FieldId(field)) return;");
    }

    [Fact]
    public void Display_set_gives_back_a_foreground_it_took_and_restores_the_position_and_registry()
    {
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "DisplayOps.cs");
        SourcePin.AssertUncommented(ops, "foreground.Check(reply);");
        SourcePin.AssertUncommented(ops, "restored[\"registry\"] = WriteScreenRegistry(b0.Registry);");
        SourcePin.AssertUncommented(ops, "baseline.Registry = ReadScreenRegistry();");
        SourcePin.AssertUncommented(ops, "reply[\"afterCall\"] = Read();");
    }

    private static string Driver() => SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.SyntheticInput.cs");

    [Fact]
    public void A_drag_start_releases_the_press_target_and_ends_click_eligibility()
    {
        string frame = SourcePin.MethodBody(Driver(), "private static void SyntheticPointerFrame(");
        int begin = frame.IndexOf("SendDrag(_synthDrag, ev, \"begin-drag\")", StringComparison.Ordinal);
        Assert.True(begin > 0);
        string after = frame.Substring(begin);
        SourcePin.AssertUncommented(after, "_synthPress.GetComponent<Selectable>()?.OnPointerUp(ev)");
        SourcePin.AssertUncommented(after, "_synthPress = null;");
        SourcePin.AssertUncommented(after, "ev.eligibleForClick = false;");
        SourcePin.AssertUncommented(after, "_synthClick = null;");
    }

    [Fact]
    public void A_core_unload_sends_pointer_up_to_the_held_selectable_before_dropping_the_state()
    {
        string ensure = SourcePin.MethodBody(Driver(), "internal static void EnsureSyntheticDriver(");
        int up = ensure.IndexOf("OnPointerUp(ev)", StringComparison.Ordinal);
        int cleared = ensure.IndexOf("UiSyntheticInput.BeforeEnd = null;", StringComparison.Ordinal);
        Assert.True(up > 0 && cleared > up, "the teardown sends pointer-up before it clears BeforeEnd");
    }

    [Fact]
    public void The_driver_compares_interop_objects_by_instance_id()
    {
        SourcePin.AssertNoStatementContaining(Driver(), "ReferenceEquals(");
    }

    [Fact]
    public void The_driver_drops_orphaned_text_and_the_lease_end_drops_owned_text()
    {
        string frame = SourcePin.MethodBody(Driver(), "private static void SyntheticFrame(");
        SourcePin.AssertUncommented(frame, "UiSyntheticInput.TextOrphaned(");
        SourcePin.AssertUncommented(SourcePin.MethodBody(Driver(), "private static void SyntheticBeforeEnd("), "DropOwnedText();");
    }

    [Fact]
    public void Dev_unload_restores_a_pending_display_set()
    {
        string plugin = SourcePin.ReadFile("src", "Progenitor.Dev", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "Ops.DisplayOps.Shutdown();");
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "DisplayOps.cs");
        SourcePin.AssertUncommented(ops, "RestoreNow(b, \"restore on unload\");");
    }

    // ---- nav, ui_read, widget_click ----------------------------------------------------------------

    [Fact]
    public void Nav_hold_takes_several_buttons_and_stick_takes_a_bounded_axis()
    {
        Assert.Equal("down+a", NavOps.HoldButtons("Down + A"));
        Assert.Throws<ArgumentException>(() => NavOps.HoldButtons("down+jump"));
        Assert.Throws<ArgumentException>(() => NavOps.HoldButtons(""));
        Assert.Equal(-0.5f, NavOps.StickY(Json("{\"y\":-0.5}")));
        Assert.Throws<ArgumentException>(() => NavOps.StickY(Json("{\"y\":0}")));
        Assert.Throws<ArgumentException>(() => NavOps.StickY(Json("{\"y\":2}")));
    }

    [Fact]
    public void Ui_read_names_what_it_reads_and_its_required_keys()
    {
        Assert.Equal("state", UiReadOps.Parse(Json("{}")).What);
        Assert.Equal("demo-table", UiReadOps.Parse(Json("{\"what\":\"widget\",\"key\":\"demo-table\"}")).Key);
        Assert.Throws<ArgumentException>(() => UiReadOps.Parse(Json("{\"what\":\"widget\"}")));
        Assert.Throws<ArgumentException>(() => UiReadOps.Parse(Json("{\"what\":\"terms\"}")));
        Assert.Throws<ArgumentException>(() => UiReadOps.Parse(Json("{\"what\":\"target\"}")));
        Assert.Equal("r", UiReadOps.Parse(Json("{\"what\":\"target\",\"row\":\"r\"}")).Target!.Row);
        Assert.Throws<ArgumentException>(() => UiReadOps.Parse(Json("{\"what\":\"everything\"}")));
    }

    [Fact]
    public void Widget_click_takes_shift_and_ctrl()
    {
        var r = ModsUiArgs.Parse(Json("{\"action\":\"widget_click\",\"label\":\"demo-table:header/c:item\",\"modifiers\":[\"shift\"]}"));
        Assert.Equal(UiInputPlan.Shift, r.Modifiers);
        Assert.Equal(0, ModsUiArgs.Parse(Json("{\"action\":\"widget_click\",\"label\":\"x\"}")).Modifiers);
    }

    // ---- display and screenshot ----------------------------------------------------------------------

    [Fact]
    public void Display_set_needs_a_size_and_names_the_modes()
    {
        var r = DisplayPlan.Parse(Json("{\"action\":\"set\",\"width\":2560,\"height\":1440,\"mode\":\"windowed\"}"));
        Assert.Equal(DisplayPlan.Action.Set, r.Action);
        Assert.Equal(DisplayPlan.Mode.Windowed, r.WindowMode);
        Assert.Equal(DisplayPlan.Mode.FullScreenWindow, DisplayPlan.ParseMode("borderless"));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseMode("exclusive"));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseMode("fullscreen"));
        Assert.Throws<ArgumentException>(() => DisplayPlan.Parse(Json("{\"action\":\"set\",\"width\":2560}")));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseMode("tiny"));
        Assert.Equal(DisplayPlan.Action.Status, DisplayPlan.Parse(Json("{}")).Action);
        Assert.Equal(DisplayPlan.Action.Restore, DisplayPlan.Parse(Json("{\"action\":\"restore\"}")).Action);
        Assert.False(r.AllowInWorld);
        Assert.True(DisplayPlan.Parse(Json("{\"action\":\"set\",\"width\":2560,\"height\":1440,\"allowInWorld\":true}")).AllowInWorld);
    }

    [Fact]
    public void Display_set_is_refused_in_a_world_unless_allowed()
    {
        Assert.NotNull(DisplayPlan.InWorldRefusal(clientWorldExists: true, allowInWorld: false));
        Assert.Null(DisplayPlan.InWorldRefusal(clientWorldExists: true, allowInWorld: true));
        Assert.Null(DisplayPlan.InWorldRefusal(clientWorldExists: false, allowInWorld: false));
    }

    [Fact]
    public void The_display_baseline_round_trips_through_its_file_and_rejects_junk()
    {
        var b = new DisplayPlan.Baseline { Width = 3840, Height = 1080, Mode = 1, SettingsWidth = 3840, SettingsHeight = 1080, SettingsWindowMode = 1, SettingsWritten = true };
        var back = DisplayPlan.ParseBaseline(DisplayPlan.SerializeBaseline(b))!;
        Assert.Equal(3840, back.Width);
        Assert.Equal(1080, back.Height);
        Assert.Equal(1, back.Mode);
        Assert.True(back.SettingsWritten);
        Assert.False(back.SettingsFileWritten);
        // A user who plays in exclusive full screen gets exactly that back.
        Assert.Equal(0, DisplayPlan.ParseBaseline(DisplayPlan.SerializeBaseline(new DisplayPlan.Baseline { Width = 1920, Height = 1080, Mode = 0 }))!.Mode);
        Assert.Null(DisplayPlan.ParseBaseline(""));
        Assert.Null(DisplayPlan.ParseBaseline("{not json"));
        Assert.Null(DisplayPlan.ParseBaseline("{\"Width\":0,\"Height\":1080,\"Mode\":3}"));
        Assert.Null(DisplayPlan.ParseBaseline("{\"Width\":1920,\"Height\":1080,\"Mode\":9}"));
    }

    [Fact]
    public void A_display_change_counts_only_when_the_window_matches()
    {
        Assert.True(DisplayPlan.Matches(1920, 1080, 3, 1920, 1080, 3));
        Assert.True(DisplayPlan.Matches(1921, 1080, 3, 1920, 1080, 3));
        Assert.False(DisplayPlan.Matches(1600, 900, 3, 1920, 1080, 3)); // the monitor did not hold it
        Assert.False(DisplayPlan.Matches(1920, 1080, 1, 1920, 1080, 3)); // the mode did not change
        Assert.True(DisplayPlan.Matches(5120, 1440, 1, 1920, 1080, 1)); // borderless takes the monitor's size
    }

    [Fact]
    public void Screenshots_take_only_a_plain_name_and_never_replace_a_file()
    {
        Assert.True(DisplayPlan.IsSafeShotName("table-1080_2.png"));
        foreach (var bad in new[] { "../x.png", "..\\x.png", "C:x.png", "a/b.png", "a\\b.png", "x.jpg", ".png", ".hidden.png", "x..png", new string('a', 81) + ".png" })
            Assert.False(DisplayPlan.IsSafeShotName(bad), bad);
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseShot(Json("{\"path\":\"C:/Windows/x.png\"}")));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseShot(Json("{\"name\":\"../evil.png\"}")));
        var taken = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            System.IO.Path.Combine("shots", "a.png"),
            System.IO.Path.Combine("shots", "a-2.png"),
        };
        Assert.Equal(System.IO.Path.Combine("shots", "a-3.png"), DisplayPlan.UniqueShotPath("shots", "a.png", DateTime.UtcNow, taken.Contains));
        Assert.Equal(System.IO.Path.Combine("shots", "b.png"), DisplayPlan.UniqueShotPath("shots", "b.png", DateTime.UtcNow, taken.Contains));
        Assert.EndsWith(".png", DisplayPlan.UniqueShotPath("shots", null, new DateTime(2026, 10, 14, 1, 2, 3, DateTimeKind.Utc), taken.Contains));
    }

    [Fact]
    public void A_capture_is_capped_at_33_megapixels()
    {
        Assert.Null(DisplayPlan.PixelRefusal(1920, 1080, 4)); // 7680x4320
        Assert.Null(DisplayPlan.PixelRefusal(3840, 2160, 2));
        Assert.NotNull(DisplayPlan.PixelRefusal(5120, 1440, 4));
        Assert.NotNull(DisplayPlan.PixelRefusal(3840, 2160, 3));
    }

    [Fact]
    public void Every_action_fits_the_bridge_cap_and_long_ones_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"x\":1,\"y\":1,\"holdFrames\":121}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"drag\",\"x\":1,\"y\":1,\"toX\":2,\"toY\":2,\"steps\":181}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"click\",\"x\":1,\"y\":1,\"settleFrames\":61}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"key\",\"key\":\"Enter\",\"frames\":31}"));
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"type\",\"text\":\"" + new string('x', 401) + "\"}"));
        var longest = Parse("{\"action\":\"drag\",\"x\":1,\"y\":1,\"toX\":2,\"toY\":2,\"steps\":180,\"settleFrames\":60}");
        Assert.True(UiInputPlan.Timeline(longest, 1, 1, 2, 2)[^1].Frame <= UiInputPlan.MaxTimelineFrames);
        Assert.True(UiInputPlan.DeadlineMs < 30_000);
        Assert.Equal(1, UiInputPlan.TypePerFrame(40));
        Assert.Equal(4, UiInputPlan.TypePerFrame(400));
        Assert.True(400 / UiInputPlan.TypePerFrame(400) <= 120);
    }

    [Fact]
    public void A_screenshot_region_is_a_rect_or_a_row_or_a_panel_and_never_a_point()
    {
        var s = DisplayPlan.ParseShot(Json("{\"rect\":{\"x\":10,\"y\":20,\"w\":30,\"h\":40},\"pad\":4,\"superSize\":2}"));
        Assert.True(s.HasRect);
        Assert.Equal(2, s.SuperSize);
        Assert.Equal(4f, s.Pad);
        Assert.Equal("demo-tips", DisplayPlan.ParseShot(Json("{\"panel\":\"demo-tips\"}")).Region!.Panel);
        Assert.Null(DisplayPlan.ParseShot(Json("{}")).Region);
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseShot(Json("{\"x\":1,\"y\":2}")));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseShot(Json("{\"superSize\":5}")));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseShot(Json("{\"name\":\"a.jpg\"}")));
        Assert.Equal("t.png", DisplayPlan.ParseShot(Json("{\"name\":\"t.png\"}")).Name);
    }

    [Fact]
    public void A_crop_scales_with_the_supersize_pads_and_clamps_to_the_image()
    {
        Assert.Equal((20, 40, 60, 80), DisplayPlan.Crop(10, 20, 30, 40, 0, 2, 3840, 2160));
        Assert.Equal((0, 0, 16, 16), DisplayPlan.Crop(2, 2, 10, 10, 4, 1, 1920, 1080));
        Assert.Equal((1910, 1070, 10, 10), DisplayPlan.Crop(1910, 1070, 50, 50, 0, 1, 1920, 1080));
        Assert.Null(DisplayPlan.Crop(5000, 10, 10, 10, 0, 1, 1920, 1080));
    }

    [Fact]
    public void Cropped_pixels_keep_their_rows_bottom_up()
    {
        // 3x2 image, 1 byte a pixel: bottom row 0 1 2, top row 3 4 5.
        var src = new byte[] { 0, 1, 2, 3, 4, 5 };
        Assert.Equal(new byte[] { 1, 2, 4, 5 }, DisplayPlan.CropPixels(src, 3, 2, 1, 1, 0, 2, 2));
        var rgb = Enumerable.Range(0, 3 * 2 * 3).Select(i => (byte)i).ToArray();
        Assert.Equal(new byte[] { 12, 13, 14 }, DisplayPlan.CropPixels(rgb, 3, 2, 3, 1, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayPlan.CropPixels(src, 3, 2, 1, 2, 0, 2, 1));
    }

    [Fact]
    public void The_png_size_comes_from_its_header()
    {
        var head = new byte[24];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }.CopyTo(head, 0);
        head[18] = 0x0F; head[19] = 0x00; // 3840
        head[22] = 0x08; head[23] = 0x70; // 2160
        Assert.Equal((3840, 2160), DisplayPlan.PngSize(head));
        Assert.Null(DisplayPlan.PngSize(new byte[10]));
    }
}
