using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b: the pick band's Escape and input on the framework's own paths, game-free: one physical Escape (27, then its
/// paired Back 17) is one action — with the Mods workspace open, the first press closes the workspace only and the pick
/// stays armed; the pick's close-key hold with the band shown and no other panel decides <see cref="UiMaskScope.CloseKeyOnly"/>;
/// and the band's panel is marked as never scrolling.
/// </summary>
[Collection("UiStatic")]
public sealed class UiWorldPickEscapeTests : IDisposable
{
    private sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        public readonly List<IRuntimePanel> NoScroll = new();

        public void NoScrollPanel(IRuntimePanel panel) => NoScroll.Add(panel);
    }

    private sealed class Blocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation => false;
        public bool DenyModifications => false;
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;
        public bool MaskLive => true;
        public bool Register() => IsRegistered = true;

        public bool Remove()
        {
            IsRegistered = false;
            return true;
        }

        public void SetMaskScope(UiMaskScope scope) => AppliedScope = scope;
        public void Dispose() => IsRegistered = false;
    }

    private readonly Runtime _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly List<IDisposable> _leases = new();

    public UiWorldPickEscapeTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiInputCapture.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var l in _leases) l.Dispose();
        foreach (var p in _panels) p.Dispose();
        UiInputCapture.ResetForTests();
        UiApi.ResetForTests();
    }

    private IUiPanel Shown(string title, UiCaptureMode mode)
    {
        var panel = UiApi.Panel(title, _owner, "example.pick");
        _panels.Add(panel);
        panel.CaptureMode = mode;
        panel.Show();
        return panel;
    }

    /// <summary>The pick's Escape handler as WorldPickBackend has it: it takes the press only when no other panel is over the band.</summary>
    private Func<bool> PickHandler(IUiPanel band, List<string> taken) => () =>
    {
        int others = UiApi.ShownPanelsSnapshot().Count(p => !ReferenceEquals(p, band));
        var owner = UiWorldPickEscape.Decide(true, false, false, false, others, false);
        if (owner != UiWorldPickEscapeOwner.Pick) return false;
        taken.Add("pick");
        return true;
    };

    [Fact]
    public void With_the_workspace_open_one_escape_closes_the_workspace_and_leaves_the_pick_armed()
    {
        var band = Shown("Pick in world", UiCaptureMode.Never);
        var workspace = Shown("Mods", UiCaptureMode.WhileShown);
        var taken = new List<string>();
        _leases.Add(UiApi.PushEscape(PickHandler(band, taken)));

        UiApi.PressEscapeForBridge(); // one physical Escape: 27, then the paired Back 17
        Assert.Equal(UiHandleState.Built, workspace.State); // the workspace closed
        Assert.Empty(taken); // and the pick did not take the same press's Back

        band.Show(); // the band comes back on the next frame (WorldPickBackend.UpdateBand)
        UiApi.PressEscapeForBridge(); // the next press is the pick's
        Assert.Equal(new[] { "pick" }, taken);
    }

    [Fact]
    public void With_the_pick_topmost_one_escape_ends_it_and_hides_nothing()
    {
        var band = Shown("Pick in world", UiCaptureMode.Never);
        var taken = new List<string>();
        _leases.Add(UiApi.PushEscape(PickHandler(band, taken)));
        UiApi.PressEscapeForBridge();
        Assert.Equal(new[] { "pick" }, taken); // once, though the press arrives as 27 and 17
        Assert.Equal(UiHandleState.Shown, band.State);
    }

    [Fact]
    public void The_band_shown_alone_with_its_close_key_hold_decides_close_key_only()
    {
        var blocker = new Blocker();
        UiInputCapture.Use(blocker);
        var band = Shown("Pick in world", UiCaptureMode.Never);
        Assert.Equal(0, UiInputCapture.Count); // a Never panel is not counted, so it cannot ask for the full mask
        UiInputCapture.AcquireCloseKey();
        Assert.True(blocker.IsRegistered);
        Assert.Equal(UiMaskScope.CloseKeyOnly, UiInputCapture.WantedMaskScope);
        Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.AppliedScope);
        Assert.Contains("scope=CloseKeyOnly", UiInputCapture.Describe(), StringComparison.Ordinal);

        // A capturing panel opened and closed over it changes nothing once it is gone.
        var workspace = Shown("Mods", UiCaptureMode.WhileShown);
        Assert.Equal(UiMaskScope.Full, blocker.AppliedScope);
        workspace.Hide();
        Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.AppliedScope);
        UiInputCapture.ReleaseCloseKey();
        Assert.False(blocker.IsRegistered);
        Assert.Equal(UiHandleState.Shown, band.State);
    }

    [Fact]
    public void A_no_scroll_panel_is_handed_to_the_runtime()
    {
        var band = UiApi.Panel("Pick in world", _owner, "example.pick");
        _panels.Add(band);
        UiApi.NoScrollPanel(band);
        Assert.Single(_runtime.NoScroll);
    }
}
