using System;
using System.Linq;
using Progenitor.Chat;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT1: the chat box's visibility time and the click guard, game-free.</summary>
[Collection("ChatClickGuard")]
public sealed class ChatWindowLogicTests : IDisposable
{
    public ChatWindowLogicTests()
    {
        ChatClickGuard.Reset();
        ChatClickGuard.Use(null);
    }

    public void Dispose()
    {
        ChatClickGuard.Reset();
        ChatClickGuard.Use(null);
    }

    [Fact]
    public void Visibility_moves_the_game_timer_so_the_fade_starts_at_the_setting()
    {
        // A message at t=100 with 30 s: the game fades 14 s after the written time, so the target is 116.
        Assert.Equal(116f, ChatVisibility.MessageTimeToWrite(now: 120f, current: 100f, lastActivity: 100f, visibleSeconds: 30, keepVisible: false));
        // Never past now: at t=105 the furthest it can go is 105.
        Assert.Equal(105f, ChatVisibility.MessageTimeToWrite(now: 105f, current: 100f, lastActivity: 100f, visibleSeconds: 30, keepVisible: false));
    }

    [Fact]
    public void Visibility_never_moves_the_game_timer_back()
    {
        Assert.Null(ChatVisibility.MessageTimeToWrite(now: 200f, current: 190f, lastActivity: 100f, visibleSeconds: 30, keepVisible: false));
        // The game's own 14 s is the floor: a setting below it changes nothing.
        Assert.Null(ChatVisibility.MessageTimeToWrite(now: 120f, current: 100f, lastActivity: 100f, visibleSeconds: 5, keepVisible: false));
    }

    [Fact]
    public void Keep_visible_writes_now_every_frame()
    {
        Assert.Equal(500f, ChatVisibility.MessageTimeToWrite(now: 500f, current: 100f, lastActivity: 0f, visibleSeconds: 30, keepVisible: true));
        Assert.Null(ChatVisibility.MessageTimeToWrite(now: 500f, current: 500f, lastActivity: 0f, visibleSeconds: 30, keepVisible: true));
    }

    [Fact]
    public void Visibility_ignores_a_bad_clock_and_replaces_a_bad_game_value()
    {
        Assert.Null(ChatVisibility.MessageTimeToWrite(float.NaN, 1f, 1f, 30, true));
        Assert.Equal(10f, ChatVisibility.MessageTimeToWrite(10f, float.NaN, 10f, 30, false));
    }

    [Fact]
    public void Visible_seconds_clamp_to_the_game_floor_and_the_cap()
    {
        Assert.Equal(14, ChatVisibility.ClampSeconds(0));
        Assert.Equal(30, ChatVisibility.ClampSeconds(30));
        Assert.Equal(600, ChatVisibility.ClampSeconds(100000));
    }

    [Fact]
    public void Guard_is_armed_while_the_pointer_rests_on_the_chat_box()
    {
        Assert.True(ChatClickGuard.Update(enabled: true, overChat: true, anyButtonHeld: false));
        Assert.True(ChatClickGuard.Active);
        Assert.False(ChatClickGuard.Update(enabled: true, overChat: false, anyButtonHeld: false));
    }

    [Fact]
    public void A_press_that_starts_on_the_chat_box_stays_blocked_until_release()
    {
        ChatClickGuard.Update(true, overChat: true, anyButtonHeld: false);
        Assert.True(ChatClickGuard.Update(true, overChat: true, anyButtonHeld: true));
        // Dragged off the box with the button still down: still the chat box's click.
        Assert.True(ChatClickGuard.Update(true, overChat: false, anyButtonHeld: true));
        Assert.False(ChatClickGuard.Update(true, overChat: false, anyButtonHeld: false));
    }

    [Fact]
    public void A_press_that_starts_elsewhere_is_never_cut_off_over_the_chat_box()
    {
        ChatClickGuard.Update(true, overChat: false, anyButtonHeld: false);
        // A camera drag or held attack crossing the chat box.
        Assert.False(ChatClickGuard.Update(true, overChat: true, anyButtonHeld: true));
        Assert.False(ChatClickGuard.Update(true, overChat: true, anyButtonHeld: true));
        // Released over the box: the next press there is the chat box's.
        Assert.True(ChatClickGuard.Update(true, overChat: true, anyButtonHeld: false));
    }

    [Fact]
    public void Disabled_or_reset_takes_nothing()
    {
        ChatClickGuard.Update(true, overChat: true, anyButtonHeld: false);
        Assert.False(ChatClickGuard.Update(enabled: false, overChat: true, anyButtonHeld: false));
        ChatClickGuard.Update(true, overChat: true, anyButtonHeld: false);
        ChatClickGuard.Reset();
        Assert.False(ChatClickGuard.Active);
        // The latch went with it: a held press after a reset is not the chat box's.
        Assert.False(ChatClickGuard.Update(true, overChat: true, anyButtonHeld: true));
    }

    [Fact]
    public void Blocked_set_without_bindings_is_the_game_defaults()
    {
        Assert.Equal(new[] { 2, 4, 100, 303, 308, 500 }, ChatClickGuard.BlockedActions());
    }

    [Fact]
    public void Blocked_set_follows_the_extra_buttons_and_skips_keys_and_non_gameplay_actions()
    {
        ChatClickGuard.Use(new (string, UiKey)[]
        {
            ("Interrupt", UiKey.Q),            // moved to a key: no longer taken
            ("AutoMove", UiKey.None),          // unbound
            ("WeaponSkill1", UiKey.MouseForward),
            ("PushToTalk", UiKey.MouseMiddle), // voice is never taken
            ("MapMarker", UiKey.MouseBack),    // not gameplay
            ("UltimateSpell", UiKey.MouseBack),
        });
        int[] blocked = ChatClickGuard.BlockedActions();
        Assert.Equal(new[] { 100, 102, 106, 303, 308, 500 }, blocked);
        Assert.DoesNotContain(5, blocked);
    }

    [Fact]
    public void Blocked_set_is_sorted_and_unique()
    {
        int[] built = ChatClickGuard.Build(new (string, UiKey)[] { ("Primary", UiKey.MouseMiddle), ("Primary", UiKey.MouseBack) });
        Assert.Equal(built.Distinct().OrderBy(x => x).ToArray(), built);
    }

    [Fact]
    public void Mask_target_picks_the_chat_context_only_for_its_own_pointer()
    {
        var ours = new IntPtr(0x1000);
        var guard = new IntPtr(0x2000);
        var chat = new IntPtr(0x3000);
        Assert.Equal(HotkeyMaskTarget.Chat, MenuHotkeyGuard.TargetFor(chat, ours, guard, chat));
        Assert.Equal(HotkeyMaskTarget.Ours, MenuHotkeyGuard.TargetFor(ours, ours, guard, chat));
        Assert.Equal(HotkeyMaskTarget.Guard, MenuHotkeyGuard.TargetFor(guard, ours, guard, chat));
        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(new IntPtr(0x4000), ours, guard, chat));
        // Zero never matches, and the three-pointer form never answers Chat.
        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(IntPtr.Zero, ours, guard, IntPtr.Zero));
        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(chat, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(chat, ours, guard));
    }

    [Fact]
    public void Chat_guard_order_is_ahead_of_chat_camera_and_gameplay()
    {
        Assert.True(UiInputCapture.ChatClickGuardOrder < 99);
        Assert.True(UiInputCapture.ChatClickGuardOrder < UiInputCapture.HotkeyGuardOrder);
    }

    [Fact]
    public void Chat_replacement_names_no_mod_by_default()
    {
        var loaded = new[] { new System.Collections.Generic.KeyValuePair<string, string>("example.chatskin", "ChatSkin") };
        Assert.Empty(ChatReplacement.Entries(""));
        Assert.Empty(ChatReplacement.Entries(null));
        Assert.Null(ChatReplacement.FirstListed(loaded, ""));
    }

    [Fact]
    public void Chat_replacement_matches_a_listed_guid_or_name_exactly_ignoring_case()
    {
        var loaded = new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("example.chatskin", "ChatSkin"),
            new System.Collections.Generic.KeyValuePair<string, string>("example.noname", ""),
        };
        Assert.Equal(new[] { "a", "b", "c" }, ChatReplacement.Entries(" a, b;;c ,").ToArray());
        Assert.Equal("ChatSkin", ChatReplacement.FirstListed(loaded, "EXAMPLE.CHATSKIN"));
        Assert.Equal("ChatSkin", ChatReplacement.FirstListed(loaded, "other; chatskin"));
        Assert.Equal("example.noname", ChatReplacement.FirstListed(loaded, "example.noname"));
        // A part of a GUID or name is not a match.
        Assert.Null(ChatReplacement.FirstListed(loaded, "chat"));
        Assert.Null(ChatReplacement.FirstListed(loaded, "example"));
        Assert.Equal(20, ChatReplacement.Entries(string.Join(",", Enumerable.Range(0, 30))).Count);
    }

    [Fact]
    public void Chat_replacement_sees_another_mod_driving_the_scroll_content()
    {
        var node = new IntPtr(0x1000);
        var clone = new IntPtr(0x2000);
        var other = new IntPtr(0x3000);
        Assert.False(ChatReplacement.ContentTakenOver(node, node, clone));
        Assert.False(ChatReplacement.ContentTakenOver(clone, node, clone));
        Assert.True(ChatReplacement.ContentTakenOver(other, node, clone));
        Assert.True(ChatReplacement.ContentTakenOver(other, node, IntPtr.Zero));
        Assert.False(ChatReplacement.ContentTakenOver(IntPtr.Zero, node, clone));
        Assert.False(ChatReplacement.ContentTakenOver(other, IntPtr.Zero, clone));
    }

    [Fact]
    public void Wheel_over_the_chat_box_blocks_only_the_camera_zoom()
    {
        // The pointer over the visible box, no button held: the wheel half is on.
        Assert.True(ChatClickGuard.UpdateWheel(overChat: true));
        Assert.True(ChatClickGuard.WheelBlocked);
        Assert.True(ChatClickGuard.AnyActive);

        var plan = ChatMaskPlan.For(clicksActive: false, wheelActive: ChatClickGuard.WheelBlocked, keyboardAndMouse: true);
        Assert.True(plan.BlockZoom);
        Assert.False(plan.BlockButtons);
        Assert.False(plan.Empty);
    }

    [Fact]
    public void Wheel_guard_does_not_depend_on_click_safe()
    {
        // ClickSafe off: the buttons half takes nothing, the wheel half still follows the pointer.
        Assert.False(ChatClickGuard.Update(enabled: false, overChat: true, anyButtonHeld: false));
        ChatClickGuard.UpdateWheel(overChat: true);
        var plan = ChatMaskPlan.For(ChatClickGuard.Active, ChatClickGuard.WheelBlocked, keyboardAndMouse: true);
        Assert.True(plan.BlockZoom);
        Assert.False(plan.BlockButtons);
    }

    [Fact]
    public void Wheel_follows_the_pointer_with_no_latch()
    {
        // A press that started on the box keeps the buttons blocked off it; the wheel goes back to the camera at once.
        ChatClickGuard.Update(true, overChat: true, anyButtonHeld: false);
        ChatClickGuard.UpdateWheel(true);
        Assert.True(ChatClickGuard.Update(true, overChat: false, anyButtonHeld: true));
        ChatClickGuard.UpdateWheel(false);
        var plan = ChatMaskPlan.For(ChatClickGuard.Active, ChatClickGuard.WheelBlocked, keyboardAndMouse: true);
        Assert.True(plan.BlockButtons);
        Assert.False(plan.BlockZoom);
    }

    [Fact]
    public void Off_the_chat_box_the_mask_takes_nothing()
    {
        ChatClickGuard.Update(true, overChat: false, anyButtonHeld: false);
        ChatClickGuard.UpdateWheel(false);
        Assert.False(ChatClickGuard.AnyActive);
        Assert.True(ChatMaskPlan.For(ChatClickGuard.Active, ChatClickGuard.WheelBlocked, keyboardAndMouse: true).Empty);
    }

    [Fact]
    public void Clicks_and_wheel_over_the_box_take_both_halves()
    {
        var plan = ChatMaskPlan.For(clicksActive: true, wheelActive: true, keyboardAndMouse: true);
        Assert.True(plan.BlockButtons);
        Assert.True(plan.BlockZoom);
    }

    [Fact]
    public void A_controller_keeps_its_zoom_and_buttons_whatever_the_mouse_rests_on()
    {
        Assert.True(ChatMaskPlan.For(clicksActive: true, wheelActive: true, keyboardAndMouse: false).Empty);
    }

    [Fact]
    public void Reset_drops_the_wheel_guard()
    {
        ChatClickGuard.UpdateWheel(true);
        ChatClickGuard.Reset();
        Assert.False(ChatClickGuard.WheelBlocked);
        Assert.False(ChatClickGuard.AnyActive);
    }

    [Fact]
    public void Chat_and_panel_masks_take_the_same_zoom_axis()
    {
        // The shared rule: every Progenitor surface that scrolls under the pointer takes ZoomCamera, and the panel
        // scope that blocks under the pointer does too.
        Assert.True(InputMaskPlan.For(InputMaskPlan.ScopeFor(pointerBlocked: true)).BlockZoom);
        Assert.False(InputMaskPlan.For(InputMaskPlan.ScopeFor(pointerBlocked: false)).BlockZoom);
        Assert.Equal(16, AnalogInputActions.ZoomCamera);
    }
}
