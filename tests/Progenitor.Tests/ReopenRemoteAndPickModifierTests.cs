using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Inventory;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// REOPEN2 (the remote fallback of <c>ctx.Inventory.ReopenLast</c>) and PICKMOD1 (the modifiers held at a world pick's
/// Interact press, and the band's modifier hint), game-free.
/// </summary>
public sealed class ReopenRemoteAndPickModifierTests
{
    // ---- REOPEN2 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(ContainerReopenResult.Moved, true)]
    [InlineData(ContainerReopenResult.Gone, true)]
    [InlineData(ContainerReopenResult.NotOpened, true)]
    [InlineData(ContainerReopenResult.ClosedByPlayer, false)] // never against the player's own close
    [InlineData(ContainerReopenResult.MenuOpen, false)]
    [InlineData(ContainerReopenResult.InputBusy, false)]
    [InlineData(ContainerReopenResult.NothingToReopen, false)]
    [InlineData(ContainerReopenResult.AlreadyOpen, false)]
    [InlineData(ContainerReopenResult.Opened, false)]
    [InlineData(ContainerReopenResult.Cancelled, false)]
    [InlineData(ContainerReopenResult.NotInGame, false)]
    public void Only_being_away_falls_back_to_the_remote_window(ContainerReopenResult result, bool remote) =>
        Assert.Equal(remote, ContainerReturnRules.FallsBackToRemote(result));

    [Fact]
    public void The_remote_outcome_follows_the_preview()
    {
        Assert.Null(ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Requesting));
        Assert.Null(ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Waiting));
        Assert.Equal(ContainerReopenResult.RemoteOpened, ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Open));
        Assert.Equal(ContainerReopenResult.RemoteFailed, ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Closed));
    }

    private sealed class FakePreview
    {
        public bool Closed;
    }

    [Fact]
    public void A_plugins_unload_takes_every_remote_preview_opened_for_it_also_one_kept_open()
    {
        var tracked = new RemotePreviewsByOwner<FakePreview>();
        object pluginA = new(), pluginB = new();
        var a1 = new FakePreview();
        var b1 = new FakePreview();
        var a2 = new FakePreview();
        tracked.Add(pluginA, a1); // reported RemoteOpened: the watch let go of it, the list still has it
        tracked.Add(pluginB, b1);
        tracked.Add(null, new FakePreview()); // Core's own: not a plugin's to close
        tracked.Add(pluginA, a2);
        Assert.Equal(3, tracked.Count);

        // A preview that closed on its own is forgotten (it never pins its plugin).
        a2.Closed = true;
        tracked.Prune(p => p.Closed);
        Assert.Equal(2, tracked.Count);

        Assert.Equal(new[] { a1 }, tracked.TakeFor(pluginA));
        Assert.Empty(tracked.TakeFor(pluginA));
        Assert.Equal(1, tracked.Count);
        Assert.Equal(new[] { b1 }, tracked.TakeAll());
        Assert.Equal(0, tracked.Count);
    }

    [Fact]
    public void The_cheap_refusals_come_before_any_lookup_in_the_same_order()
    {
        var off = new ContainerPreviewFacts { UserConnected = true, Mode = ContainerPreviewMode.Off, RateOk = true };
        Assert.NotNull(ContainerPreviewAuthorization.RequestRefusal(off));
        Assert.Equal(ContainerPreviewAuthorization.Refusal(off), ContainerPreviewAuthorization.RequestRefusal(off));
        var flood = new ContainerPreviewFacts { UserConnected = true, Mode = ContainerPreviewMode.On, RateOk = false };
        Assert.Equal(ContainerPreviewAuthorization.Refusal(flood), ContainerPreviewAuthorization.RequestRefusal(flood));
        Assert.NotNull(ContainerPreviewAuthorization.RequestRefusal(new ContainerPreviewFacts { Mode = ContainerPreviewMode.On, RateOk = true }));
        // Connected, allowed, a token: nothing yet about the container (the lookup decides the rest).
        var ok = new ContainerPreviewFacts { UserConnected = true, Mode = ContainerPreviewMode.On, RateOk = true };
        Assert.Null(ContainerPreviewAuthorization.RequestRefusal(ok));
        Assert.NotNull(ContainerPreviewAuthorization.Refusal(ok));
    }

    [Fact]
    public void The_remote_fallback_is_opt_in()
    {
        var options = new ContainerReopenOptions();
        Assert.False(options.RemoteIfAway);
        Assert.Null(options.RemotePreview);
        var finished = new ContainerReopenFinished(ContainerReopenResult.RemoteFailed, "This server has remote chest preview turned off.", null);
        Assert.Equal("RemoteFailed: This server has remote chest preview turned off.", finished.ToString());
        Assert.Equal("Opened", new ContainerReopenFinished(ContainerReopenResult.Opened, null!, null).ToString());
    }

    [Fact]
    public void A_preview_by_network_id_has_its_own_refusal_and_the_reply_carries_the_id()
    {
        Assert.False(string.IsNullOrWhiteSpace(ContainerPreviewAuthorization.UnknownByNetworkId));
        var reply = new ContainerPreviewPacket { Kind = ContainerPreviewWire.KindReply, Sequence = 4, Ok = true, Container = "c-" + new string('a', 32) };
        var back = ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(reply), expectRequest: false);
        Assert.NotNull(back);
        Assert.Equal(reply.Container, back!.Container);
        // A request by network id: no container text, the words instead; it decodes as a request.
        var request = new ContainerPreviewPacket { Kind = ContainerPreviewWire.KindOpen, Sequence = 5, Container = "", NetIndex = 1234, NetGeneration = 7, NetType = 1, Seconds = 180 };
        var req = ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(request), expectRequest: true);
        Assert.NotNull(req);
        Assert.Equal("", req!.Container);
        Assert.Equal(1234, req.NetIndex);
    }

    // ---- PICKMOD1 --------------------------------------------------------------------------------

    [Fact]
    public void The_hint_splits_into_the_key_cap_and_the_words_with_the_interact_key_put_in()
    {
        Assert.Equal(("Shift+F", "except"), UiWorldPickCopy.SplitHint("Shift+{interact}: except", "F"));
        Assert.Equal(("Shift+E", "except"), UiWorldPickCopy.SplitHint("  Shift+{Interact} :  except ".Replace(" :", ":"), "E"));
        Assert.Equal(("Ctrl+Interact", "all of them"), UiWorldPickCopy.SplitHint("Ctrl+{interact}: all of them", ""));
        Assert.Equal(("", "Hold Shift to skip"), UiWorldPickCopy.SplitHint("Hold Shift to skip", "F"));
        Assert.Equal(("", ""), UiWorldPickCopy.SplitHint("  ", "F"));
        Assert.Equal(("", ""), UiWorldPickCopy.SplitHint(null, "F"));
    }

    [Fact]
    public void A_modifier_let_go_just_before_the_pick_still_counts()
    {
        var m = new UiWorldPickModifiers();
        m.Note(0.00, KeyModifiers.None);
        m.Note(0.02, KeyModifiers.Shift);
        m.Note(0.04, KeyModifiers.None); // let go
        Assert.Equal(KeyModifiers.Shift, m.At(0.05));
        Assert.Equal(KeyModifiers.Shift, m.At(0.02 + UiWorldPickModifiers.WindowSeconds));
        Assert.Equal(KeyModifiers.None, m.At(0.03 + UiWorldPickModifiers.WindowSeconds));
        m.Note(1.0, KeyModifiers.Ctrl | KeyModifiers.Alt);
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Alt, m.At(1.0));
        m.Clear();
        Assert.Equal(KeyModifiers.None, m.At(1.0));
    }

    [Fact]
    public void A_shift_still_held_from_the_last_pick_does_not_reach_the_next()
    {
        var m = new UiWorldPickModifiers();
        m.Note(0.00, KeyModifiers.Shift);
        Assert.Equal(KeyModifiers.Shift, m.Deliver(0.02, KeyModifiers.Shift));
        // Shift let go slowly after the pick: still held for a few frames, then a plain press.
        m.Note(0.04, KeyModifiers.Shift);
        m.Note(0.06, KeyModifiers.Shift);
        Assert.Equal(KeyModifiers.None, m.Deliver(0.08, KeyModifiers.None));
        // Held across both picks, never let go: not the next press's either.
        m.Note(0.10, KeyModifiers.Shift);
        Assert.Equal(KeyModifiers.None, m.Deliver(0.12, KeyModifiers.Shift));
        // Let go (one clean frame), pressed again: it counts.
        m.Note(0.14, KeyModifiers.None);
        m.Note(0.16, KeyModifiers.Shift);
        Assert.Equal(KeyModifiers.Shift, m.Deliver(0.18, KeyModifiers.Shift));
    }

    [Fact]
    public void A_frame_hitch_does_not_drop_the_shift_held_at_the_press()
    {
        var m = new UiWorldPickModifiers();
        m.Note(0.0, KeyModifiers.None);
        // No tick sampled for longer than the window (a hitch); Shift is held at the press itself.
        Assert.Equal(KeyModifiers.Shift, m.Deliver(1.0, KeyModifiers.Shift));
        // Arming a new pick starts fresh: a modifier already held counts.
        m.Clear();
        Assert.Equal(KeyModifiers.Ctrl, m.Deliver(2.0, KeyModifiers.Ctrl));
    }

    private sealed class Backend : IUiWorldPickBackend
    {
        public bool Ready(out string why) { why = ""; return true; }
        public bool StillReady(out string why) { why = ""; return true; }
        void IUiWorldPickBackend.Armed(UiWorldPickHandle pick) { }
        void IUiWorldPickBackend.Disarmed() { }
        public void Repicked(UiWorldPickHandle pick) { }
    }

    [Fact]
    public void Each_pick_carries_the_modifiers_of_its_own_press_also_when_repeating()
    {
        var picked = new List<UiWorldPickResult>();
        var c = new UiWorldPickCoordinator(new Backend(), () => 0, () => 3, _ => { });
        c.Arm(this, "Test", picked.Add, null, new UiWorldPickOptions { Repeat = true, ModifierHint = " Shift+{interact}: except " });
        Assert.Equal("Shift+{interact}: except", c.Current!.Options.ModifierHint);
        c.Complete(UiWorldPickMenus.ContainerMenu, 10, 1, 3, true, modifiers: KeyModifiers.Shift);
        c.Complete(UiWorldPickMenus.ContainerMenu, 11, 1, 3, true);
        c.Complete(UiWorldPickMenus.ContainerMenu, 12, 1, 3, true, modifiers: KeyModifiers.Ctrl | KeyModifiers.Shift);
        Assert.Equal(new[] { true, false, true }, picked.Select(r => r.Shift));
        Assert.Equal(new[] { false, false, true }, picked.Select(r => r.Ctrl));
        Assert.False(picked[0].Alt);
        Assert.Equal(KeyModifiers.None, picked[1].Modifiers);
        Assert.Contains("held Shift", picked[0].ToString());
        Assert.DoesNotContain("held", picked[1].ToString());
    }

    [Fact]
    public void The_hint_is_empty_by_default()
    {
        Assert.Equal("", new UiWorldPickOptions().ModifierHint);
        Assert.Equal(KeyModifiers.None, default(UiWorldPickResult).Modifiers);
    }

    private sealed class HeldSource : IKeyInputSource
    {
        public ModifierSides Held;
        public bool IsAvailable => true;
        public ChatFocus ChatFocus => ChatFocus.NotFocused;
        public bool TextFieldFocused => false;
        public bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr) { refusedAsAltGr = false; return false; }
        public bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides) { chord = KeyChord.None; sides = ModifierSides.None; return false; }
        public bool TryReadHeldModifiers(out ModifierSides held) { held = Held; return true; }
    }

    [Fact]
    public void The_held_modifiers_come_from_the_framework_key_source()
    {
        var source = new HeldSource { Held = ModifierSides.RightShift | ModifierSides.LeftCtrl };
        Keybinds.SetSourceForTests(source);
        try
        {
            Assert.Equal(KeyModifiers.Shift | KeyModifiers.Ctrl, Keybinds.HeldModifiers());
            source.Held = ModifierSides.None;
            Assert.Equal(KeyModifiers.None, Keybinds.HeldModifiers());
        }
        finally { Keybinds.SetSourceForTests(null); }
    }
}
