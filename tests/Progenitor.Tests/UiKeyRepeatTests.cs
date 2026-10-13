using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Held-key auto-repeat for the shown-input pump. Time is advanced explicitly;
/// these tests assert character counts and timings, not that a method was called.
/// </summary>
public class UiKeyRepeatTests
{
    [Fact]
    public void Repeatable_kinds_are_insert_backspace_delete_and_arrows_only()
    {
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Insert));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Backspace));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Delete));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Left));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Right));
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Modifier));
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Shortcut));
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.Home));
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.End));
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedCommandKind.None));
        Assert.Equal(400, UiKeyRepeat.InitialDelayMs);
        Assert.Equal(50, UiKeyRepeat.RepeatIntervalMs);
    }

    [Fact]
    public void A_short_tap_fires_the_edge_only()
    {
        var repeat = new UiKeyRepeat();
        var text = new UiTextEditState("abcde", caret: 5, anchor: 5);

        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: true, nowMs: 0));
        text = UiTextEdit.Backspace(text);
        Assert.Equal("abcd", text.Text);

        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 50));
        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 399));
        Assert.Equal("abcd", text.Text);

        Assert.False(repeat.Observe(UiKey.Backspace, held: false, edge: false, nowMs: 400));
        Assert.Equal("abcd", text.Text);
        Assert.Equal(4, text.Text.Length);
    }

    [Fact]
    public void Holding_backspace_deletes_at_the_initial_delay_then_the_repeat_interval()
    {
        var repeat = new UiKeyRepeat();
        var text = new UiTextEditState("abcde", caret: 5, anchor: 5);

        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: true, nowMs: 0));
        text = UiTextEdit.Backspace(text);
        Assert.Equal(4, text.Text.Length);
        Assert.Equal("abcd", text.Text);

        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 399));
        Assert.Equal(4, text.Text.Length);

        Assert.True(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 400));
        text = UiTextEdit.Backspace(text);
        Assert.Equal(3, text.Text.Length);
        Assert.Equal("abc", text.Text);

        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 449));
        Assert.Equal(3, text.Text.Length);

        Assert.True(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 450));
        text = UiTextEdit.Backspace(text);
        Assert.Equal(2, text.Text.Length);
        Assert.Equal("ab", text.Text);

        Assert.True(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 500));
        text = UiTextEdit.Backspace(text);
        Assert.Equal(1, text.Text.Length);
        Assert.Equal("a", text.Text);
    }

    [Fact]
    public void Release_stops_repeat_immediately()
    {
        var repeat = new UiKeyRepeat();
        var text = new UiTextEditState("abcde", caret: 5, anchor: 5);

        repeat.Observe(UiKey.Backspace, held: true, edge: true, nowMs: 0);
        text = UiTextEdit.Backspace(text);
        Assert.Equal(4, text.Text.Length);

        Assert.False(repeat.Observe(UiKey.Backspace, held: false, edge: false, nowMs: 200));
        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 400));
        Assert.Equal(4, text.Text.Length);
        Assert.Equal("abcd", text.Text);
    }

    [Fact]
    public void Focus_loss_stops_an_in_flight_repeat()
    {
        var repeat = new UiKeyRepeat();
        var text = new UiTextEditState("abcde", caret: 5, anchor: 5);

        repeat.Observe(UiKey.Backspace, held: true, edge: true, nowMs: 0);
        text = UiTextEdit.Backspace(text);
        Assert.Equal(4, text.Text.Length);

        repeat.Reset();
        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 400));
        Assert.False(repeat.Observe(UiKey.Backspace, held: true, edge: false, nowMs: 800));
        Assert.Equal(4, text.Text.Length);
        Assert.Equal("abcd", text.Text);
    }

    [Fact]
    public void Holding_a_letter_repeats_it_after_the_delay()
    {
        var repeat = new UiKeyRepeat();
        var text = new UiTextEditState("", caret: 0, anchor: 0);

        Assert.False(repeat.Observe(UiKey.A, held: true, edge: true, nowMs: 0));
        text = UiTextEdit.Insert(text, "a");
        Assert.Equal("a", text.Text);

        Assert.False(repeat.Observe(UiKey.A, held: true, edge: false, nowMs: 399));
        Assert.Equal("a", text.Text);

        Assert.True(repeat.Observe(UiKey.A, held: true, edge: false, nowMs: 400));
        text = UiTextEdit.Insert(text, "a");
        Assert.Equal("aa", text.Text);

        Assert.True(repeat.Observe(UiKey.A, held: true, edge: false, nowMs: 450));
        text = UiTextEdit.Insert(text, "a");
        Assert.Equal("aaa", text.Text);
    }

    [Fact]
    public void Shortcuts_and_modifiers_do_not_repeat()
    {
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedKeys.Decide(UiKey.A, ctrl: true, alt: false, meta: false, shift: false).Kind));
        Assert.False(UiKeyRepeat.IsRepeatable(UiTypedKeys.Decide(UiKey.LeftCtrl, ctrl: true, alt: false, meta: false, shift: false).Kind));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedKeys.Decide(UiKey.A, ctrl: false, alt: false, meta: false, shift: false).Kind));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedKeys.Decide(UiKey.Delete, ctrl: false, alt: false, meta: false, shift: false).Kind));
        Assert.True(UiKeyRepeat.IsRepeatable(UiTypedKeys.Decide(UiKey.LeftArrow, ctrl: false, alt: false, meta: false, shift: false).Kind));
    }
}
