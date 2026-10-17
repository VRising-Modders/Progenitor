using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <see cref="UiWorldPickOptions.CloseGameWindows"/> on the game-free coordinator: the open game window is closed once,
/// after the readiness check and before the pick arms, for one-shot and repeating picks; never when the option is off or
/// the pick cannot arm; and only an interact window or the character inventory counts.
/// </summary>
public sealed class UiWorldPickCloseWindowsTests
{
    private sealed class Backend : IUiWorldPickBackend
    {
        public readonly List<string> Calls = new();
        public bool IsReady = true;
        public int OpenMenu = UiWorldPickMenus.ContainerMenu;
        public bool Throws;

        public bool Ready(out string why)
        {
            Calls.Add("ready");
            why = IsReady ? "" : "no client world";
            return IsReady;
        }

        public bool StillReady(out string why)
        {
            why = "";
            return true;
        }

        public void Armed(UiWorldPickHandle pick) => Calls.Add("armed");

        public void Disarmed() => Calls.Add("disarmed");

        public int CloseGameWindows()
        {
            Calls.Add("close");
            if (Throws) throw new System.InvalidOperationException("menu system gone");
            int menu = UiWorldPickMenus.ClosesForPick(OpenMenu) ? OpenMenu : 0;
            if (menu > 0) OpenMenu = 0;
            return menu;
        }
    }

    private static (UiWorldPickCoordinator Coordinator, Backend Backend, List<string> Log) Rig()
    {
        var backend = new Backend();
        var log = new List<string>();
        return (new UiWorldPickCoordinator(backend, () => 0, () => 1, log.Add), backend, log);
    }

    [Fact]
    public void The_open_window_closes_after_the_readiness_check_and_before_the_pick_arms()
    {
        var (c, backend, log) = Rig();
        var pick = c.Arm(new object(), "Test", _ => { }, null, new UiWorldPickOptions { CloseGameWindows = true });
        Assert.Equal(new[] { "ready", "close", "armed" }, backend.Calls);
        Assert.Equal(UiWorldPickState.Armed, pick.State);
        Assert.Equal(UiWorldPickMenus.ContainerMenu, pick.ClosedMenu);
        Assert.Contains(log, l => l.Contains("closed the open game window (menu 3)"));
    }

    [Fact]
    public void Off_by_default_and_never_when_the_pick_cannot_arm()
    {
        var (c, backend, _) = Rig();
        Assert.False(new UiWorldPickOptions().CloseGameWindows);
        c.Arm(new object(), "Test", _ => { }, null, null);
        Assert.DoesNotContain("close", backend.Calls);

        var (c2, backend2, _) = Rig();
        backend2.IsReady = false;
        var pick = c2.Arm(new object(), "Test", _ => { }, null, new UiWorldPickOptions { CloseGameWindows = true });
        Assert.Equal(UiWorldPickState.Cancelled, pick.State);
        Assert.DoesNotContain("close", backend2.Calls);
    }

    [Fact]
    public void A_repeating_pick_closes_the_window_once_and_keeps_picking()
    {
        var (c, backend, _) = Rig();
        var picked = new List<int>();
        var pick = c.Arm(new object(), "Test", r => picked.Add(r.EntityIndex), null,
            new UiWorldPickOptions { CloseGameWindows = true, Repeat = true });
        Assert.NotNull(c.Complete(UiWorldPickMenus.ContainerMenu, 10, 1, 1, suppressed: true));
        Assert.NotNull(c.Complete(UiWorldPickMenus.WorkstationMenu, 11, 1, 1, suppressed: true));
        Assert.Equal(new[] { 10, 11 }, picked);
        Assert.Equal(UiWorldPickState.Armed, pick.State);
        Assert.Single(backend.Calls, "close");
    }

    [Fact]
    public void Nothing_open_or_a_failed_close_still_arms_the_pick()
    {
        var (c, backend, _) = Rig();
        backend.OpenMenu = 0;
        var pick = c.Arm(new object(), "Test", _ => { }, null, new UiWorldPickOptions { CloseGameWindows = true });
        Assert.Equal(UiWorldPickState.Armed, pick.State);
        Assert.Equal(0, pick.ClosedMenu);

        var (c2, backend2, log2) = Rig();
        backend2.Throws = true;
        var pick2 = c2.Arm(new object(), "Test", _ => { }, null, new UiWorldPickOptions { CloseGameWindows = true });
        Assert.Equal(UiWorldPickState.Armed, pick2.State);
        Assert.Contains(log2, l => l.Contains("could not close the game window"));
    }

    [Theory]
    [InlineData(UiWorldPickMenus.CharacterMenu, true)]
    [InlineData(UiWorldPickMenus.ContainerMenu, true)]
    [InlineData(UiWorldPickMenus.WorkstationMenu, true)]
    [InlineData(UiWorldPickMenus.RefinementstationMenu, true)]
    [InlineData(UiWorldPickMenus.CastleHeartMenu, true)]
    [InlineData(0, false)]  // nothing open
    [InlineData(-1, false)] // the menu system could not be read
    [InlineData(2, false)]  // another menu (not an interact window, not the inventory)
    public void Only_an_interact_window_or_the_inventory_is_closed(int menuType, bool closes) =>
        Assert.Equal(closes, UiWorldPickMenus.ClosesForPick(menuType));

    [Fact]
    public void The_option_survives_the_copy_the_coordinator_keeps()
    {
        var copy = new UiWorldPickOptions { CloseGameWindows = true, Repeat = true }.Copy();
        Assert.True(copy.CloseGameWindows);
        Assert.True(copy.Repeat);
    }
}
