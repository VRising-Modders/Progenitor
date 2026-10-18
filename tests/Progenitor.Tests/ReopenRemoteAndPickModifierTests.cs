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
        Assert.Null(ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Requesting, false));
        Assert.Null(ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Waiting, false));
        Assert.Equal(ContainerReopenResult.RemoteOpened, ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Open, false));
        Assert.Equal(ContainerReopenResult.RemoteFailed, ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Closed, false));
        Assert.Equal(ContainerReopenResult.RemoteOpened, ContainerReturnRules.RemoteOutcome(ContainerPreviewState.Closed, true));
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
