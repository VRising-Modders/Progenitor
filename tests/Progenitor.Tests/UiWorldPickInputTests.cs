using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the pick band's input footprint. The band is never routed by the pointer (<see cref="UiCaptureMode.Never"/>), so it
/// can never ask for the full mask; while the pick owns Escape it holds a close-key-only context
/// (<see cref="UiInputCapture.AcquireCloseKey"/>), which takes Back / Menu_Close and nothing else — the player moves, aims
/// and zooms while picking. Game-free: the mask decision, the hold's interaction with shown panels and the exclusive hold,
/// and the precedence that decides when the band holds it.
/// </summary>
[Collection("PluginLogger")]
public sealed class UiWorldPickInputTests : IDisposable
{
    private sealed class Blocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation { get; private set; }
        public bool DenyModifications => false;
        public UiMaskScope Applied { get; private set; } = UiMaskScope.Full;
        public UiMaskScope AppliedScope => Applied;
        public bool MaskLive => true;

        public bool Register()
        {
            IsRegistered = true;
            BlocksCameraRotation = InputMaskPlan.For(Applied).BlockCameraRotationFallback;
            return true;
        }

        public bool Remove()
        {
            IsRegistered = false;
            BlocksCameraRotation = false;
            return true;
        }

        public void SetMaskScope(UiMaskScope scope)
        {
            Applied = scope;
            BlocksCameraRotation = InputMaskPlan.For(scope).BlockCameraRotationFallback;
        }

        public void Dispose() => IsRegistered = false;
    }

    private readonly Blocker _blocker = new();

    public UiWorldPickInputTests()
    {
        UiInputCapture.ResetForTests();
        UiInputCapture.Use(_blocker);
    }

    public void Dispose() => UiInputCapture.ResetForTests();

    [Fact]
    public void A_close_key_hold_registers_the_context_and_masks_only_the_close_key()
    {
        UiInputCapture.SetPointerBlock(true); // no readable pointer: the fail-closed gate a WhenHovered band fell into
        UiInputCapture.AcquireCloseKey();
        Assert.True(UiInputCapture.IsBlocking); // a context is wanted (Escape must be taken)
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(UiMaskScope.CloseKeyOnly, UiInputCapture.WantedMaskScope);
        Assert.Equal(UiMaskScope.CloseKeyOnly, _blocker.Applied);
        Assert.False(UiInputCapture.IsFullyBlocking); // keybinds still fire while picking

        var plan = InputMaskPlan.For(_blocker.Applied);
        Assert.False(plan.BlockAllKeys);
        Assert.False(plan.BlockMoveAndAim);
        Assert.False(plan.BlockZoom);
        Assert.False(_blocker.BlocksCameraRotation);

        UiInputCapture.ReleaseCloseKey();
        Assert.False(UiInputCapture.IsBlocking);
        Assert.False(_blocker.IsRegistered);
        UiInputCapture.ReleaseCloseKey(); // never below zero
        Assert.Equal(0, UiInputCapture.CloseKeyCount);
    }

    [Fact]
    public void A_shown_panel_still_masks_by_the_pointer_beside_a_hold_and_takes_nothing_more_once_it_closes()
    {
        UiInputCapture.AcquireCloseKey();
        UiInputCapture.Acquire(); // the Mods workspace opens over the pick
        UiInputCapture.SetPointerBlock(true);
        Assert.Equal(UiMaskScope.Full, _blocker.Applied); // the panel's own rule, unchanged
        UiInputCapture.SetPointerBlock(false);
        Assert.Equal(UiMaskScope.CloseKeyOnly, _blocker.Applied);
        UiInputCapture.SetPointerBlock(true);
        UiInputCapture.Release(); // the workspace closes; the pick stays armed
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(UiMaskScope.CloseKeyOnly, _blocker.Applied);
        UiInputCapture.AcquireExclusive(); // photo mode still wins
        Assert.Equal(UiMaskScope.Exclusive, _blocker.Applied);
        UiInputCapture.ReleaseExclusive();
        Assert.Equal(UiMaskScope.CloseKeyOnly, _blocker.Applied);
    }

    /// <summary>
    /// Every sequence of the mutators, the close-key hold among them, to depth four: the blocker always applies what the
    /// module decided, the full mask only ever with a shown panel under the pointer, and a hold alone never more than the
    /// close key.
    /// </summary>
    [Fact]
    public void Every_sequence_with_holds_keeps_the_full_mask_to_shown_panels()
    {
        var ops = new (string Name, Action Run)[]
        {
            ("acquire", UiInputCapture.Acquire),
            ("release", UiInputCapture.Release),
            ("hold", UiInputCapture.AcquireCloseKey),
            ("unhold", UiInputCapture.ReleaseCloseKey),
            ("pointer-on", () => UiInputCapture.SetPointerBlock(true)),
            ("pointer-off", () => UiInputCapture.SetPointerBlock(false)),
            ("disable", () => UiInputCapture.SetEnabled(false)),
            ("enable", () => UiInputCapture.SetEnabled(true)),
        };
        const int depth = 4;
        int total = (int)Math.Pow(ops.Length, depth);
        var trail = new string[depth];
        for (int seq = 0; seq < total; seq++)
        {
            var blocker = new Blocker();
            UiInputCapture.ResetForTests();
            UiInputCapture.Use(blocker);
            int n = seq;
            for (int step = 0; step < depth; step++)
            {
                var op = ops[n % ops.Length];
                n /= ops.Length;
                trail[step] = op.Name;
                op.Run();
                string where = string.Join(" > ", trail, 0, step + 1);
                Assert.True(blocker.Applied == UiInputCapture.WantedMaskScope, where);
                bool mayBeFull = UiInputCapture.Count > 0 && UiInputCapture.Enabled && UiInputCapture.PointerBlock;
                Assert.True((blocker.Applied == UiMaskScope.Full) == mayBeFull, where + ": " + blocker.Applied);
                bool wanted = UiInputCapture.Enabled && (UiInputCapture.Count > 0 || UiInputCapture.CloseKeyCount > 0);
                Assert.True(wanted == UiInputCapture.IsBlocking, where);
            }
        }
    }

    [Fact]
    public void The_band_is_never_pointer_routed_and_holds_the_close_key_only_while_the_pick_owns_escape()
    {
        foreach (UiWorldPickEscapeOwner owner in Enum.GetValues(typeof(UiWorldPickEscapeOwner)))
        {
            Assert.Equal(UiCaptureMode.Never, UiWorldPickEscape.BandMode(owner));
            Assert.Equal(owner == UiWorldPickEscapeOwner.Pick, UiWorldPickEscape.BandHoldsCloseKey(owner));
        }

        Assert.Equal(UiMaskScope.CloseKeyOnly, UiWorldPickEscape.BandScope);
        // A Never panel asks the pointer gate for nothing, even with no readable pointer (a controller player).
        var band = new List<HoverPanel> { new(UiCaptureMode.Never, ScreenRect.FromCorners(0f, -8f, 0f, -8f)) };
        Assert.False(HoverRouter.ShouldBlock(band, PointerSample.Unreadable));
        Assert.False(HoverRouter.ShouldBlock(band, PointerSample.At(960f, 540f)));
    }
}
