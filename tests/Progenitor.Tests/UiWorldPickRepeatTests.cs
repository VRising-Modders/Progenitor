using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the repeating world pick (<see cref="UiWorldPickOptions.Repeat"/>) on the game-free coordinator: each matching
/// interact delivers and the pick stays armed, the count, the same-target rule, Escape as "finished", the idle timeout,
/// the band's words, and the one-shot pick left exactly as it was.
/// </summary>
public sealed class UiWorldPickRepeatTests
{
    private sealed class Backend : IUiWorldPickBackend
    {
        public int Armed, Disarmed;
        public readonly List<int> Repicks = new();
        public bool Alive = true;

        public bool Ready(out string why)
        {
            why = "";
            return true;
        }

        public bool StillReady(out string why)
        {
            why = Alive ? "" : "the client world is gone";
            return Alive;
        }

        void IUiWorldPickBackend.Armed(UiWorldPickHandle pick) => Armed++;

        void IUiWorldPickBackend.Disarmed() => Disarmed++;

        public void Repicked(UiWorldPickHandle pick) => Repicks.Add(pick.PickCount);
    }

    private sealed class Rig
    {
        public readonly Backend Backend = new();
        public double Now;
        public int Epoch = 3;
        public readonly List<string> Log = new();
        public readonly UiWorldPickCoordinator Coordinator;
        public readonly List<UiWorldPickResult> Picked = new();
        public int Cancelled;

        public Rig() => Coordinator = new UiWorldPickCoordinator(Backend, () => Now, () => Epoch, Log.Add);

        public UiWorldPickHandle Arm(UiWorldPickOptions? options = null) =>
            Coordinator.Arm(this, "Test", Picked.Add, () => Cancelled++, options ?? new UiWorldPickOptions { Repeat = true });

        public UiWorldPickResult? Interact(int index, int version = 1, int menu = UiWorldPickMenus.ContainerMenu) =>
            Coordinator.Offer(menu) == UiWorldPickOffer.PassThrough ? null : Coordinator.Complete(menu, index, version, Epoch, suppressed: true);
    }

    [Fact]
    public void Each_interact_delivers_and_the_same_handle_stays_armed()
    {
        var rig = new Rig();
        var pick = rig.Arm();
        Assert.NotNull(rig.Interact(10));
        Assert.NotNull(rig.Interact(11));
        Assert.NotNull(rig.Interact(12, menu: UiWorldPickMenus.WorkstationMenu));

        Assert.Equal(UiWorldPickState.Armed, pick.State);
        Assert.True(pick.Active);
        Assert.Same(pick, rig.Coordinator.Current);
        Assert.Equal(3, pick.PickCount);
        Assert.Equal(new[] { 10, 11, 12 }, rig.Picked.Select(r => r.EntityIndex));
        Assert.Equal(new[] { 1, 2, 3 }, rig.Backend.Repicks); // the band is told each new count
        Assert.Equal(0, rig.Backend.Disarmed);
        Assert.Equal(1, rig.Backend.Armed);
        Assert.Equal(0, rig.Cancelled);
    }

    [Fact]
    public void Escape_finishes_a_repeating_pick_as_cancelled_with_its_count()
    {
        var rig = new Rig();
        var pick = rig.Arm();
        rig.Interact(10);
        rig.Interact(11);
        int countWhenFinished = -1;
        UiWorldPickHandle? pick2 = null;
        // onCancelled is the "finished" signal; the count is final when it runs.
        pick2 = rig.Coordinator.Arm(this, "Test", _ => { }, () => countWhenFinished = pick2!.PickCount, new UiWorldPickOptions { Repeat = true });
        Assert.Equal(UiWorldPickState.Cancelled, pick.State); // replaced
        Assert.Equal(1, rig.Cancelled);
        Assert.Equal(2, pick.PickCount);

        rig.Coordinator.Complete(UiWorldPickMenus.ContainerMenu, 1, 1, rig.Epoch, true);
        Assert.True(rig.Coordinator.Escape());
        Assert.Equal(UiWorldPickState.Cancelled, pick2.State);
        Assert.Equal(1, pick2.PickCount);
        Assert.Equal(1, countWhenFinished);
        Assert.Contains(rig.Log, l => l.Contains("finished by Escape after 1 picks", StringComparison.Ordinal));
        Assert.Null(rig.Coordinator.Current);
        Assert.Equal(2, rig.Backend.Disarmed);
    }

    [Fact]
    public void The_same_target_twice_in_a_row_is_reported_once_within_the_swallow_window()
    {
        var rig = new Rig();
        var pick = rig.Arm();
        Assert.NotNull(rig.Interact(10));
        rig.Now = 2;
        Assert.Null(rig.Interact(10)); // the same chest again
        Assert.Equal(1, pick.PickCount);
        Assert.Equal(UiWorldPickState.Armed, pick.State);
        rig.Now = 3;
        Assert.NotNull(rig.Interact(10, version: 2)); // another entity (a new version) is another target
        Assert.NotNull(rig.Interact(11));
        Assert.NotNull(rig.Interact(10)); // not in a row any more
        rig.Now = 3 + UiWorldPickSwallowList.Ttl + 0.1;
        Assert.NotNull(rig.Interact(10)); // the window passed
        Assert.Equal(5, pick.PickCount);
        Assert.Contains(rig.Log, l => l.Contains("ignored a repeat of the same target 10:1", StringComparison.Ordinal));
    }

    [Fact]
    public void The_timeout_counts_from_the_last_pick()
    {
        var rig = new Rig();
        var pick = rig.Arm(new UiWorldPickOptions { Repeat = true, TimeoutSeconds = 5 });
        rig.Now = 4;
        rig.Interact(10);
        rig.Now = 8; // 8 s since arming, 4 since the pick
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.Armed, pick.State);
        rig.Now = 9.1;
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.TimedOut, pick.State);
        Assert.Equal(1, rig.Cancelled);
        Assert.Equal(1, pick.PickCount);
    }

    [Fact]
    public void Leaving_the_world_cancel_and_dispose_end_a_repeating_pick_as_a_one_shot_one()
    {
        var rig = new Rig();
        var a = rig.Arm();
        rig.Interact(1);
        rig.Backend.Alive = false;
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.Cancelled, a.State);
        Assert.Equal(1, rig.Cancelled);

        rig.Backend.Alive = true;
        var b = rig.Arm();
        b.Cancel();
        Assert.Equal(UiWorldPickState.Cancelled, b.State);
        Assert.Equal(2, rig.Cancelled);

        var c = rig.Arm();
        rig.Interact(2);
        c.Dispose();
        Assert.Equal(UiWorldPickState.Disposed, c.State);
        Assert.Equal(2, rig.Cancelled); // dispose runs no callback
        Assert.Equal(1, c.PickCount);

        var d = rig.Arm();
        rig.Epoch++;
        Assert.Null(rig.Interact(3)); // armed in another world
        Assert.Equal(UiWorldPickState.Cancelled, d.State);
        Assert.Equal(0, d.PickCount);
    }

    [Fact]
    public void An_unwanted_kind_passes_through_and_a_callback_may_finish_the_pick()
    {
        var rig = new Rig();
        UiWorldPickHandle? pick = null;
        pick = rig.Coordinator.Arm(this, "Test", r =>
        {
            if (r.EntityIndex == 99) pick!.Cancel(); // the caller decides it has enough
        }, () => rig.Cancelled++, new UiWorldPickOptions { Repeat = true });
        Assert.Null(rig.Interact(1, menu: UiWorldPickMenus.CastleHeartMenu)); // not a wanted kind
        Assert.Equal(0, pick.PickCount);
        rig.Interact(5);
        rig.Interact(99);
        Assert.Equal(UiWorldPickState.Cancelled, pick.State);
        Assert.Equal(2, pick.PickCount);
        Assert.Equal(1, rig.Cancelled);
        Assert.Null(rig.Interact(6)); // nothing armed
    }

    [Fact]
    public void A_one_shot_pick_is_unchanged()
    {
        var rig = new Rig();
        var pick = rig.Arm(new UiWorldPickOptions());
        Assert.NotNull(rig.Interact(10));
        Assert.Equal(UiWorldPickState.Picked, pick.State);
        Assert.Equal(1, pick.PickCount);
        Assert.Empty(rig.Backend.Repicks);
        Assert.Equal(1, rig.Backend.Disarmed);
        Assert.Null(rig.Interact(11));
        Assert.Single(rig.Picked);
        Assert.Contains(rig.Log, l => l == "Ui: world pick #1 picked " + rig.Picked[0] + ".");
        Assert.Equal(0, UiWorldPickHandle.Inert().PickCount);
    }

    [Fact]
    public void Options_and_band_words_follow_the_mode()
    {
        Assert.Equal(UiWorldPickOptions.DefaultPrompt, new UiWorldPickOptions().Copy().Prompt);
        Assert.False(new UiWorldPickOptions().Copy().Repeat);
        var repeat = new UiWorldPickOptions { Repeat = true }.Copy();
        Assert.True(repeat.Repeat);
        Assert.Equal(UiWorldPickOptions.DefaultRepeatPrompt, repeat.Prompt);
        Assert.Equal("Add chests", new UiWorldPickOptions { Repeat = true, Prompt = " Add chests " }.Copy().Prompt);

        Assert.Equal("", UiWorldPickCopy.BandCount(repeat: false, count: 3)); // a one-shot band never counts
        Assert.Equal("", UiWorldPickCopy.BandCount(repeat: true, count: 0));
        Assert.Equal("3 picked", UiWorldPickCopy.BandCount(repeat: true, count: 3));
        Assert.Equal(UiWorldPickCopy.PickWord, UiWorldPickCopy.InteractWord(repeat: false));
        Assert.Equal("Add", UiWorldPickCopy.InteractWord(repeat: true));
        Assert.Equal(UiWorldPickCopy.CancelWord, UiWorldPickCopy.EscapeWord(repeat: false));
        Assert.Equal("Finish", UiWorldPickCopy.EscapeWord(repeat: true));
    }

    /// <summary>
    /// The band's bottom edge sits above the HUD: the health readout and the buff row above the action bar reach about
    /// 180 units up on the 1080 design canvas (the mockup review found a band whose bottom was lower collided with them).
    /// </summary>
    [Theory]
    [InlineData(300f)]
    [InlineData(700f)]
    [InlineData(1200f)]
    public void The_band_is_at_least_980_wide_and_fades_to_clear_at_both_ends(float content)
    {
        float w = UiWorldPickBandLook.Width(content);
        Assert.True(w >= UiWorldPickBandLook.MinWidth);
        Assert.True(w >= content + 2f * UiWorldPickBandLook.PadX - 1f, "the content reaches into the fade");
        Assert.Equal(0f, UiWorldPickBandLook.Fade(0f));
        Assert.Equal(0f, UiWorldPickBandLook.Fade(1f));
        Assert.Equal(1f, UiWorldPickBandLook.Fade(0.5f));
        Assert.Equal(1f, UiWorldPickBandLook.Fade(UiWorldPickBandLook.FadeShare + 0.01f));
        Assert.Equal(UiWorldPickBandLook.Fade(0.07f), UiWorldPickBandLook.Fade(0.93f), 4); // symmetric
        Assert.True(UiWorldPickBandLook.Fade(0.03f) < UiWorldPickBandLook.Fade(0.10f)); // rising toward the middle
        Assert.True(UiWorldPickBandLook.FadeAlpha(0) < 0.05f && UiWorldPickBandLook.FadeAlpha(UiWorldPickBandLook.FadeTextureWidth / 2) == 1f);
        Assert.Equal(1f, UiWorldPickBandLook.RuleHeight); // thin rules, top and bottom
    }

    [Fact]
    public void The_band_is_anchored_clear_of_the_hud()
    {
        Assert.True(UiWorldPickCopy.BandBottom >= UiWorldPickCopy.MinBandBottom);
        Assert.True(UiWorldPickCopy.MinBandBottom >= 196f);
    }
}
