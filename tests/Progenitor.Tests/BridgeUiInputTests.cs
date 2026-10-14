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
        Assert.Throws<ArgumentException>(() => Parse("{\"action\":\"key\",\"key\":\"Escape\",\"modifiers\":[\"shift\"]}"));
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
        Assert.False(UiSyntheticInput.TakeEscapeForward());
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
        Assert.Equal(-120f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(-240f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0f, UiSyntheticInput.SampleWheel());
        Assert.Equal(0, UiSyntheticInput.PendingNotches);
    }

    [Fact]
    public void A_key_tap_is_held_for_its_frames_and_escape_is_forwarded_once()
    {
        Assert.Null(UiSyntheticInput.Begin("bridge", 10_000));
        Assert.True(UiSyntheticInput.Tap(UiKey.Escape, 100, 2));
        Assert.False(UiSyntheticInput.KeyHeld(UiKey.Escape, 99));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Escape, 100));
        Assert.True(UiSyntheticInput.KeyHeld(UiKey.Escape, 101));
        Assert.False(UiSyntheticInput.KeyHeld(UiKey.Escape, 102));
        Assert.True(UiSyntheticInput.TakeEscapeForward());
        Assert.False(UiSyntheticInput.TakeEscapeForward());
        UiSyntheticInput.PruneKeys(102);
        Assert.Equal(0, UiSyntheticInput.HeldKeyCount);
        Assert.True(UiSyntheticInput.Tap(UiKey.Enter, 5));
        Assert.False(UiSyntheticInput.TakeEscapeForward());
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
    public void Navigation_holds_and_the_stick_expire_on_their_own_clock()
    {
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
        Assert.Equal(DisplayPlan.Mode.ExclusiveFullScreen, DisplayPlan.ParseMode("exclusive"));
        Assert.Throws<ArgumentException>(() => DisplayPlan.Parse(Json("{\"action\":\"set\",\"width\":2560}")));
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseMode("tiny"));
        Assert.Equal(DisplayPlan.Action.Status, DisplayPlan.Parse(Json("{}")).Action);
        Assert.Equal(DisplayPlan.Action.Restore, DisplayPlan.Parse(Json("{\"action\":\"restore\"}")).Action);
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
        Assert.Throws<ArgumentException>(() => DisplayPlan.ParseShot(Json("{\"path\":\"a.jpg\"}")));
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
